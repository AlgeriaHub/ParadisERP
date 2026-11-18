using ParadiERP.Application.Contracts;
using ParadiERP.Domain;

namespace ParadiERP.Tests.Rentals;

/// <summary>
/// The paperwork every rental test now has to carry since the owner's answers of 2026-09-30:
/// a site on the contract (answer 9), the signatory's whole identity at the first issue note
/// (answer 10), and the driver and truck on both notes (answer 16).
///
/// <para>
/// Filled in here once, so a test about the arithmetic still reads as the arithmetic, and the
/// rules are met by the rules working — the tests that are <i>about</i> those rules leave these
/// helpers out and assert the refusal.
/// </para>
/// </summary>
public static class RentalTestPaper
{
    public const string DriverName = "Rachid Mansouri";
    public const string DriverPhone = "0555 12 34 56";
    public const string TruckNumber = "01234-116-16";

    /// <summary>A note with a driver and a truck, unless the test named its own.</summary>
    public static IssueNoteRequest Driven(IssueNoteRequest r) => r with
    {
        DriverName = r.DriverName ?? DriverName,
        DriverPhone = r.DriverPhone ?? DriverPhone,
        TruckNumber = r.TruckNumber ?? TruckNumber
    };

    /// <inheritdoc cref="Driven(IssueNoteRequest)"/>
    public static ReturnNoteRequest Driven(ReturnNoteRequest r) => r with
    {
        DriverName = r.DriverName ?? DriverName,
        DriverPhone = r.DriverPhone ?? DriverPhone,
        TruckNumber = r.TruckNumber ?? TruckNumber
    };

    /// <summary>The site <c>ReceivableHarness</c> gives its contractor, so its contracts can name one.</summary>
    public static readonly Guid ReceivableContractorSite = Guid.Parse("5e5e5e5e-0000-0000-0000-000000000001");

    /// <summary>A contract for the receivable harness's contractor, at his site.</summary>
    public static UpsertRentalContractRequest ContractorSited(UpsertRentalContractRequest r) => Sited(r, ReceivableContractorSite);

    /// <summary>A contract for one of the customer's sites, unless the test named its own.</summary>
    public static UpsertRentalContractRequest Sited(UpsertRentalContractRequest r, Guid siteId) =>
        r with { SiteId = r.SiteId ?? siteId };

    /// <summary>A site of the customer, open.</summary>
    public static CustomerSite Site(Guid id, Guid customerId, string name = "Chantier 120 logements") =>
        new() { Id = id, CustomerId = customerId, Name = name, Address = "Zone industrielle, lot 14" };

    /// <summary>The five identity fields of a person on file, so the first issue note is not refused.</summary>
    public static Customer Identified(Customer customer)
    {
        customer.IdDocumentType ??= "nationalCard";
        customer.IdDocumentNumber ??= "109 876 543";
        customer.IdDocumentIssuer ??= "Daïra de Bab El Oued";
        customer.IdDocumentIssuedOn ??= new DateOnly(2019, 5, 12);
        customer.BirthDate ??= new DateTime(1980, 3, 4);
        return customer;
    }
}
