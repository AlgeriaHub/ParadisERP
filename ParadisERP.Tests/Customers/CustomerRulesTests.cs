using ParadisERP.Application.Customers;
using ParadisERP.Domain;

namespace ParadisERP.Tests.Customers;

/// <summary>
/// The person / company rules on their own, with nothing behind them. These are the checks the
/// customers screen runs before saving and the server runs again; one reason code each, so the
/// screen's sentence and the server's are the same answer.
/// </summary>
public class CustomerRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Kind_parses_case_insensitively_and_empty_means_unspecified()
    {
        Assert.True(CustomerRules.TryParseKind("company", out var company));
        Assert.Equal(CustomerKind.Company, company);
        Assert.True(CustomerRules.TryParseKind(" PERSON ", out var person));
        Assert.Equal(CustomerKind.Person, person);

        // The request default: a body that says nothing has not decided.
        Assert.True(CustomerRules.TryParseKind(null, out var none));
        Assert.Equal(CustomerKind.Unspecified, none);
        Assert.True(CustomerRules.TryParseKind("", out var empty));
        Assert.Equal(CustomerKind.Unspecified, empty);
    }

    [Fact]
    public void An_unknown_kind_string_is_refused()
    {
        // Refused, not defaulted: a typo must not register a company as "we do not know".
        Assert.False(CustomerRules.TryParseKind("Societe", out _));
        Assert.False(CustomerRules.TryParseKind("7", out _));
    }

    [Fact]
    public void A_new_customer_must_say_whether_it_is_a_person_or_a_company()
    {
        Assert.Equal("kindRequired", CustomerRules.KindProblem(
            isNew: true, CustomerKind.Unspecified, CustomerKind.Unspecified, false, false));
        Assert.Null(CustomerRules.KindProblem(isNew: true, CustomerKind.Unspecified, CustomerKind.Person, false, false));
    }

    [Fact]
    public void An_existing_unspecified_customer_can_be_edited_without_choosing_yet()
    {
        // Fixing a phone number on a legacy row must not force the decision; the chip stays.
        Assert.Null(CustomerRules.KindProblem(
            isNew: false, CustomerKind.Unspecified, CustomerKind.Unspecified, false, false));
    }

    [Fact]
    public void A_kind_cannot_go_back_to_unspecified()
    {
        Assert.Equal("kindCannotRevert", CustomerRules.KindProblem(
            isNew: false, CustomerKind.Person, CustomerKind.Unspecified, false, false));
        Assert.Equal("kindCannotRevert", CustomerRules.KindProblem(
            isNew: false, CustomerKind.Company, CustomerKind.Unspecified, false, true));
    }

    [Fact]
    public void A_person_with_a_representative_is_refused()
    {
        Assert.Equal("personHasNoRepresentative", CustomerRules.KindProblem(
            isNew: true, CustomerKind.Unspecified, CustomerKind.Person, representativeInBody: true, false));
        // And so is a legacy row left unspecified: nobody signs for "we do not know".
        Assert.Equal("personHasNoRepresentative", CustomerRules.KindProblem(
            isNew: false, CustomerKind.Unspecified, CustomerKind.Unspecified, representativeInBody: true, false));
    }

    [Fact]
    public void A_company_without_a_current_representative_needs_one_in_the_body()
    {
        Assert.Equal("companyNeedsRepresentative", CustomerRules.KindProblem(
            isNew: true, CustomerKind.Unspecified, CustomerKind.Company, false, false));
        // The migrated row's case: the decision is taken when the kind is set, in the same save.
        Assert.Equal("companyNeedsRepresentative", CustomerRules.KindProblem(
            isNew: false, CustomerKind.Unspecified, CustomerKind.Company, false, false));
        Assert.Null(CustomerRules.KindProblem(
            isNew: false, CustomerKind.Unspecified, CustomerKind.Company, representativeInBody: true, false));
        // A company turned person had its representative ended; turning it back appoints again.
        Assert.Equal("companyNeedsRepresentative", CustomerRules.KindProblem(
            isNew: false, CustomerKind.Person, CustomerKind.Company, false, hasCurrentRepresentative: true));
    }

    [Fact]
    public void A_company_with_a_current_representative_refuses_a_second_one_in_the_body()
    {
        Assert.Equal("representativeExists", CustomerRules.KindProblem(
            isNew: false, CustomerKind.Company, CustomerKind.Company, representativeInBody: true, hasCurrentRepresentative: true));
        // Editing the company's phone number sends no representative and is fine.
        Assert.Null(CustomerRules.KindProblem(
            isNew: false, CustomerKind.Company, CustomerKind.Company, false, hasCurrentRepresentative: true));
    }

    // ---------- the tax number is for companies only (owner, 2026-10-05) ----------

    [Fact]
    public void A_new_person_is_refused_a_tax_number_and_a_new_company_is_not()
    {
        Assert.Equal("taxIdCompanyOnly", CustomerRules.TaxIdProblem(
            isNew: true, CustomerKind.Unspecified, null, CustomerKind.Person, "000116001234567"));
        Assert.Null(CustomerRules.TaxIdProblem(isNew: true, CustomerKind.Unspecified, null, CustomerKind.Person, null));
        // Blank is none, not a number.
        Assert.Null(CustomerRules.TaxIdProblem(isNew: true, CustomerKind.Unspecified, null, CustomerKind.Person, "   "));
        Assert.Null(CustomerRules.TaxIdProblem(
            isNew: true, CustomerKind.Unspecified, null, CustomerKind.Company, "000116001234567"));
    }

    [Fact]
    public void A_person_saved_before_the_rule_keeps_their_number_unchanged_but_cannot_change_it()
    {
        const string stored = "000116001234567";
        Assert.Null(CustomerRules.TaxIdProblem(isNew: false, CustomerKind.Person, stored, CustomerKind.Person, stored));
        // The form trims what it sends; a stored number with spaces around it is the same number.
        Assert.Null(CustomerRules.TaxIdProblem(isNew: false, CustomerKind.Person, $" {stored} ", CustomerKind.Person, stored));
        Assert.Null(CustomerRules.TaxIdProblem(isNew: false, CustomerKind.Person, stored, CustomerKind.Person, null));
        Assert.Equal("taxIdCompanyOnly", CustomerRules.TaxIdProblem(
            isNew: false, CustomerKind.Person, stored, CustomerKind.Person, "000116009999999"));
        Assert.Equal("taxIdCompanyOnly", CustomerRules.TaxIdProblem(
            isNew: false, CustomerKind.Person, null, CustomerKind.Person, stored));
    }

    [Fact]
    public void Becoming_a_person_clears_the_number_in_the_same_save()
    {
        const string stored = "000116001234567";
        Assert.Equal("taxIdCompanyOnly", CustomerRules.TaxIdProblem(
            isNew: false, CustomerKind.Company, stored, CustomerKind.Person, stored));
        Assert.Null(CustomerRules.TaxIdProblem(isNew: false, CustomerKind.Company, stored, CustomerKind.Person, ""));
        // A legacy row being decided is a person from this save on, like a new one.
        Assert.Equal("taxIdCompanyOnly", CustomerRules.TaxIdProblem(
            isNew: false, CustomerKind.Unspecified, stored, CustomerKind.Person, stored));
        // Left unspecified, it behaves as it did before the rule.
        Assert.Null(CustomerRules.TaxIdProblem(isNew: false, CustomerKind.Unspecified, stored, CustomerKind.Unspecified, stored));
        Assert.Null(CustomerRules.TaxIdProblem(isNew: false, CustomerKind.Unspecified, null, CustomerKind.Unspecified, stored));
    }

    [Fact]
    public void RepresentativeProblem_names_the_blank_name_the_early_start_and_the_future_start()
    {
        var currentFrom = Now.AddMonths(-6);

        Assert.Equal("nameRequired", CustomerRules.RepresentativeProblem("  ", Now, currentFrom, Now));
        Assert.Equal("startsBeforeCurrent",
            CustomerRules.RepresentativeProblem("Nadia Bouzid", currentFrom.AddDays(-1), currentFrom, Now));
        Assert.Null(CustomerRules.RepresentativeProblem("Nadia Bouzid", currentFrom, currentFrom, Now));
        Assert.Null(CustomerRules.RepresentativeProblem("Nadia Bouzid", Now, currentFrom, Now));

        // The form sends a company-time-zone day, and UTC midnight of "today" can be hours ahead
        // of UTC now: one day of slack, and not a minute more.
        Assert.Null(CustomerRules.RepresentativeProblem("Nadia Bouzid", Now.AddHours(20), currentFrom, Now));
        Assert.Equal("startsInFuture",
            CustomerRules.RepresentativeProblem("Nadia Bouzid", Now.AddDays(1).AddMinutes(1), currentFrom, Now));

        // The first appointment has nothing to start after.
        Assert.Null(CustomerRules.RepresentativeProblem("Karim Ben Ali", Now.AddYears(-3), null, Now));
    }
}
