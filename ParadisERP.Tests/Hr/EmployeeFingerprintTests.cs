using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Hr;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Hr;

/// <summary>
/// FR-3A: the number an employee is enrolled under on the fingerprint terminal. Optional, one
/// spelling, and never two people's — the import that reads the terminal's export matches on it
/// and on nothing else.
/// </summary>
public class EmployeeFingerprintTests
{
    private static UpsertEmployeeRequest Card(string name, string? fingerprint, Guid? branch = null) =>
        new(name, branch ?? HrHarness.Algiers, "Monthly", 52_000m, "Cash", new DateOnly(2020, 1, 1),
            FingerprintNumber: fingerprint);

    [Theory]
    [InlineData("  17 ", "17")]
    [InlineData("0007", "7")]
    [InlineData("000", "0")]
    [InlineData("١٢", "12")]
    [InlineData("۰۳", "3")]
    [InlineData("A-07", "A-07")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_number_has_one_spelling(string? typed, string? stored) =>
        Assert.Equal(stored, FingerprintNumbers.Normalize(typed));

    [Fact]
    public async Task It_is_optional_and_saved_in_its_one_spelling()
    {
        await using var h = await HrHarness.NewAsync();

        var none = await h.Employees.CreateAsync(Card("Ali Benali", null));
        var some = await h.Employees.CreateAsync(Card("Omar Ziane", " 0042 "));

        Assert.True(none.Ok, none.Message);
        Assert.Null(none.Value!.FingerprintNumber);
        Assert.True(some.Ok, some.Message);
        Assert.Equal("42", some.Value!.FingerprintNumber);
        Assert.Equal("42", (await h.Employees.ReadAsync(some.Value.Id))!.FingerprintNumber);
        // Any number of people may have none; the unique index lets NULL repeat.
        Assert.True((await h.Employees.CreateAsync(Card("Karim Haddad", null))).Ok);
    }

    [Fact]
    public async Task Two_people_never_share_one_across_the_company_and_the_refusal_names_the_holder()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.Employees.CreateAsync(Card("Ali Benali", "42"));

        // Another branch, another spelling of the same number: still Ali's.
        var refused = await h.Employees.CreateAsync(Card("Omar Ziane", "0042", HrHarness.Oran));

        Assert.Equal(HrFailure.Conflict, refused.Failure);
        Assert.Equal(HrProblems.FingerprintTaken, refused.Code);
        Assert.Equal(ali.Value!.Number, refused.Key);
        Assert.Equal(1, await h.Db.Employees.CountAsync());
    }

    [Fact]
    public async Task Someone_who_left_keeps_theirs_until_their_card_is_cleared()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = (await h.Employees.CreateAsync(Card("Ali Benali", "42"))).Value!;
        await h.Employees.SetLeftAsync(ali.Id, new EmployeeLeaveRequest(new DateOnly(2026, 3, 20)));

        Assert.Equal(HrProblems.FingerprintTaken, (await h.Employees.CreateAsync(Card("Omar Ziane", "42"))).Code);

        var cleared = await h.Employees.UpdateAsync(ali.Id, Card("Ali Benali", null) with
        {
            IsActive = false, LeftOn = new DateOnly(2026, 3, 20)
        });
        Assert.True(cleared.Ok, cleared.Message);
        Assert.True((await h.Employees.CreateAsync(Card("Omar Ziane", "42"))).Ok);
    }

    [Fact]
    public async Task Saving_a_card_again_keeps_its_own_number_and_cannot_take_another()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = (await h.Employees.CreateAsync(Card("Ali Benali", "42"))).Value!;
        await h.Employees.CreateAsync(Card("Omar Ziane", "43"));

        Assert.True((await h.Employees.UpdateAsync(ali.Id, Card("Ali Benali", "42"))).Ok);
        var refused = await h.Employees.UpdateAsync(ali.Id, Card("Ali Benali", "43"));

        Assert.Equal(HrProblems.FingerprintTaken, refused.Code);
        Assert.Equal("42", (await h.Employees.ReadAsync(ali.Id))!.FingerprintNumber);
    }

    [Fact]
    public async Task A_number_too_long_to_store_is_refused_rather_than_cut()
    {
        await using var h = await HrHarness.NewAsync();

        var refused = await h.Employees.CreateAsync(Card("Ali Benali", new string('9', FingerprintNumbers.MaxLength + 1)));

        Assert.Equal(HrProblems.FingerprintTooLong, refused.Code);
        Assert.Empty(await h.Db.Employees.ToListAsync());
    }

    [Fact]
    public async Task The_list_finds_a_person_by_their_fingerprint_number()
    {
        await using var h = await HrHarness.NewAsync();
        await h.Employees.CreateAsync(Card("Ali Benali", "42"));
        await h.Employees.CreateAsync(Card("Omar Ziane", "142"));

        var page = await h.Employees.ListAsync(null, null, "0042", 0, 50);

        Assert.Equal("Ali Benali", Assert.Single(page.Rows).FullName);
    }
}
