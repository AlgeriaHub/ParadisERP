using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Infrastructure.Hr;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Hr;

/// <summary>
/// The employee's name in two parts (owner, PinSay #301, 2026-10-05: "i need name and family name in
/// separate inputs"; printed first name first). The full name every list, account and payslip reads is
/// composed from the parts on every save; an older client that sends the name whole still saves, and
/// files it the way the migration filed everyone already on file — as the family name.
/// </summary>
public class EmployeeNamePartsTests
{
    private static UpsertEmployeeRequest Request(string fullName, string? first, string? family) =>
        new(fullName, HrHarness.Algiers, "Monthly", 52_000m, "Cash", new DateOnly(2020, 1, 1),
            FirstName: first, FamilyName: family);

    [Fact]
    public async Task The_full_name_is_the_first_name_then_the_family_name()
    {
        await using var h = await HrHarness.NewAsync();

        // What the client composed is ignored: the parts are the name.
        var created = await h.Employees.CreateAsync(Request("whatever the screen sent", "  Karim ", " Haddad  "));

        Assert.True(created.Ok, created.Message);
        Assert.Equal("Karim Haddad", created.Value!.FullName);
        Assert.Equal("Karim", created.Value.FirstName);
        Assert.Equal("Haddad", created.Value.FamilyName);
        var stored = await h.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == created.Value.Id);
        Assert.Equal(("Karim", "Haddad", "Karim Haddad"), (stored.FirstName, stored.FamilyName, stored.FullName));

        // And an edit recomposes it.
        var edited = await h.Employees.UpdateAsync(created.Value.Id, Request("", "Karim", "Haddad-Benali"));
        Assert.True(edited.Ok, edited.Message);
        Assert.Equal("Karim Haddad-Benali", edited.Value!.FullName);
    }

    [Theory]
    [InlineData("Karim", null)]
    [InlineData("Karim", "   ")]
    [InlineData(null, "Haddad")]
    [InlineData("", "Haddad")]
    public async Task Sent_in_parts_both_are_required(string? first, string? family)
    {
        await using var h = await HrHarness.NewAsync();

        var refused = await h.Employees.CreateAsync(Request("Karim Haddad", first, family));

        Assert.Equal(HrFailure.Invalid, refused.Failure);
        Assert.Equal(HrProblems.NameRequired, refused.Code);
        Assert.Equal(0, await h.Db.Employees.CountAsync());
    }

    [Fact]
    public async Task An_older_client_sending_the_name_whole_files_it_as_the_family_name()
    {
        await using var h = await HrHarness.NewAsync();

        var created = await h.Employees.CreateAsync(Request("  Karim Haddad ", null, null));

        Assert.True(created.Ok, created.Message);
        Assert.Equal("Karim Haddad", created.Value!.FullName);
        Assert.Null(created.Value.FirstName);
        Assert.Equal("Karim Haddad", created.Value.FamilyName);

        Assert.Equal(HrProblems.NameRequired, (await h.Employees.CreateAsync(Request("  ", null, null))).Code);
    }
}
