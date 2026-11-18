using System.Globalization;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Files;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Tests.Files;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Rentals.RentalTestPaper;

namespace ParadiERP.Tests.Rentals;

/// <summary>
/// The owner's paperwork answers of 2026-09-30, wired to a database: the site on every contract
/// (9), the driver and the truck on both notes (16), the delivery hour and the return day (5, 6),
/// the replacement value per item (13), the Arabic name beside the French (28) and the scan of the
/// signatory's identity document (10a). Same harness as the rest of the document rules.
/// </summary>
public partial class RentalDocumentTests
{
    private static DateTime Utc(string iso) => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ---------- the site ----------

    [Fact]
    public async Task A_new_contract_must_name_one_of_the_customer_s_open_sites()
    {
        await using var h = await NewAsync();

        var none = await h.Rentals.CreateContractAsync(Contract() with { SiteId = null }, "admin");
        Assert.Equal(RentalFailure.Invalid, none.Failure);
        Assert.Equal("A contract names the site the goods go to: choose one of the customer's sites, or add a new one.", none.Message);

        var someoneElses = await h.Rentals.CreateContractAsync(Contract() with { SiteId = CompanySite }, "admin");
        Assert.Equal("That site is not one of this customer's sites.", someoneElses.Message);

        var closed = await h.Rentals.CreateContractAsync(Contract() with { SiteId = ClosedSite }, "admin");
        Assert.Equal("The site Ancien chantier is closed. Choose an open site, or reopen it first.", closed.Message);
        Assert.Empty(await h.Db.RentalContracts.AsNoTracking().ToListAsync());

        var created = await h.Rentals.CreateContractAsync(Contract(), "admin");
        Assert.True(created.Ok, created.Message);
        var stored = await StoredAsync(h, created.Value!.Id);
        Assert.Equal(ContractorSite, stored.SiteId);
        Assert.Equal("Chantier 120 logements", stored.SiteName);
        Assert.Equal("Zone industrielle, lot 14", stored.SiteAddress);

        // A snapshot: renaming the site afterwards does not rewrite the paper.
        var site = await h.Db.CustomerSites.FirstAsync(s => s.Id == ContractorSite);
        site.Name = "Chantier renommé";
        await h.Db.SaveChangesAsync();
        Assert.Equal("Chantier 120 logements", (await StoredAsync(h, created.Value.Id)).SiteName);
    }

    [Fact]
    public async Task A_draft_keeps_its_site_and_one_raised_before_the_rule_is_edited_without_one()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;

        var dropped = await h.Rentals.UpdateContractAsync(contract.Id, Contract() with { SiteId = null });
        Assert.Equal(RentalFailure.Invalid, dropped.Failure);

        // The site it already names may have been closed since; it stays valid on its own paper.
        var site = await h.Db.CustomerSites.FirstAsync(s => s.Id == ContractorSite);
        site.IsActive = false;
        await h.Db.SaveChangesAsync();
        Assert.True((await h.Rentals.UpdateContractAsync(contract.Id, Contract() with { Notes = "tel." })).Ok);

        // As the migration leaves a contract signed before sites existed.
        var row = await h.Db.RentalContracts.FirstAsync(c => c.Id == contract.Id);
        row.SiteId = null;
        row.SiteName = null;
        row.SiteAddress = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        Assert.True((await h.Rentals.UpdateContractAsync(contract.Id, Contract() with { SiteId = null })).Ok);
        Assert.Null((await StoredAsync(h, contract.Id)).SiteName);
    }

    [Fact]
    public async Task The_truck_named_on_the_contract_is_kept_trimmed()
    {
        await using var h = await NewAsync();
        var created = await h.Rentals.CreateContractAsync(
            Contract() with { TruckType = " Semi-remorque ", TruckNumber = " 01234-116-16 ", TruckOwnerPhone = "  " }, "admin");
        var stored = await StoredAsync(h, created.Value!.Id);
        Assert.Equal("Semi-remorque", stored.TruckType);
        Assert.Equal("01234-116-16", stored.TruckNumber);
        Assert.Null(stored.TruckOwnerPhone);
    }

    // ---------- the driver and the truck on each note ----------

    [Fact]
    public async Task An_issue_note_is_refused_without_the_driver_his_phone_and_the_truck()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        var refused = await h.Rentals.IssueAsync(contract.Id,
            new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Day("2026-03-01"), DriverName: "Rachid"),
            null, "storeman");
        Assert.Equal(RentalFailure.Invalid, refused.Failure);
        Assert.Equal(
            "An issue note needs the driver's name, the driver's phone and the truck number: the driver's phone and the truck number are missing. " +
            "When the customer carries the goods himself, mark the note as transported by the customer instead.",
            refused.Message);
        Assert.Empty(await h.Db.IssueNotes.AsNoTracking().ToListAsync());
        Assert.Equal(0m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);

        var issued = await h.Rentals.IssueAsync(contract.Id,
            new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Day("2026-03-01"),
                DriverName: " Rachid Mansouri ", DriverPhone: "0555 12 34 56", TruckNumber: "99887-114-16"),
            null, "storeman");
        Assert.True(issued.Ok, issued.Message);
        var note = await h.Db.IssueNotes.AsNoTracking().SingleAsync();
        Assert.Equal("Rachid Mansouri", note.DriverName);
        Assert.Equal("0555 12 34 56", note.DriverPhone);
        Assert.Equal("99887-114-16", note.TruckNumber);
    }

    [Fact]
    public async Task Goods_the_customer_carries_himself_need_no_driver_and_keep_none()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        // Owner, 2026-10-02: mandatory when the branch transports; otherwise "transported by the customer".
        var issued = await h.Rentals.IssueAsync(contract.Id,
            new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Day("2026-03-01"),
                DriverName: "typed by mistake", CustomerTransport: true),
            null, "storeman");
        Assert.True(issued.Ok, issued.Message);
        var note = await h.Db.IssueNotes.AsNoTracking().SingleAsync();
        Assert.True(note.CustomerTransport);
        Assert.Null(note.DriverName);

        var back = await h.Rentals.ReturnAsync(contract.Id,
            new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 10)], Day("2026-03-10"),
                CustomerTransport: true), null, null);
        Assert.True(back.Ok, back.Message);
        Assert.True((await h.Db.ReturnNotes.AsNoTracking().SingleAsync()).CustomerTransport);
    }

    [Fact]
    public async Task A_return_note_is_refused_without_the_driver_his_phone_and_the_truck()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        await h.Rentals.IssueAsync(contract.Id, Issue(lineId), null, null);

        var refused = await h.Rentals.ReturnAsync(contract.Id,
            new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 10)], Day("2026-03-10")), null, null);
        Assert.Equal(RentalFailure.Invalid, refused.Failure);
        Assert.StartsWith(
            "A return note needs the driver's name, the driver's phone and the truck number: " +
            "the driver's name, the driver's phone and the truck number are missing.",
            refused.Message);
        Assert.Empty(await h.Db.ReturnNotes.AsNoTracking().ToListAsync());

        var back = await h.Rentals.ReturnAsync(contract.Id,
            Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 10)], Day("2026-03-10"))), null, null);
        Assert.True(back.Ok, back.Message);
        Assert.Equal(TruckNumber, (await h.Db.ReturnNotes.AsNoTracking().SingleAsync()).TruckNumber);
    }

    // ---------- the delivery hour and the return day ----------

    /// <summary>
    /// The owner's example on real documents: cutoff at noon, one unit for three days at 3,000 a
    /// month, out on 26 September at 15:00 in Algiers (14:00 UTC).
    /// </summary>
    private static async Task<(Harness H, Guid ContractId, Guid LineId)> OwnersExampleAsync()
    {
        var h = await NewAsync();
        var settings = await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, 0, DayCutoffHour: 12));
        Assert.True(settings.Ok, settings.Message);

        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 1, days: 3), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        var issued = await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 1)], Utc("2026-09-26T14:00:00"))), null, "storeman");
        Assert.True(issued.Ok, issued.Message);
        // The full period, three days, invoiced as the goods go out — the hour moves the start, not the price.
        Assert.Equal(300m, issued.Value!.PeriodRent);
        return (h, contract.Id, lineId);
    }

    private static ReturnNoteRequest BackAt(Guid lineId, string utc, bool? countReturnDay = null, string? reason = null) =>
        Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 1)], Utc(utc))
        {
            CountReturnDay = countReturnDay, ReturnDayReason = reason
        });

    [Fact]
    public async Task Goods_out_after_the_cutoff_start_the_next_day()
    {
        var (h, contractId, _) = await OwnersExampleAsync();
        await using var _h = h;

        var docs = (await h.Rentals.ReadAsync(contractId))!;
        Assert.Equal(new DateOnly(2026, 9, 27), h.Rentals.PeriodStartsOn(docs, docs.IssueNotes.Single()));
    }

    [Fact]
    public async Task The_rent_is_booked_on_the_first_day_of_the_period_not_the_day_it_is_typed()
    {
        // Out on 26 September at 15:00 under a noon cutoff, so the period starts on the 27th —
        // and the whole period is that day's income (owner, 2026-10-31, REN-02): midnight in
        // Algiers, 23:00 UTC the evening before. The note was typed today; the rent is September's.
        var (h, _, _) = await OwnersExampleAsync();
        await using var _h = h;

        var entry = await h.Db.LedgerEntries.AsNoTracking().SingleAsync(e => e.SourceType == LedgerSources.RentalIssue);
        Assert.Equal(new DateTime(2026, 9, 26, 23, 0, 0, DateTimeKind.Utc), entry.EntryDate);
    }

    [Fact]
    public async Task Goods_out_before_the_cutoff_are_booked_on_their_own_day()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var issued = await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            Algiers, [new IssueNoteLineRequest(contract.Lines.Single().Id, 10)], Day("2026-03-01"))), null, "storeman");
        Assert.True(issued.Ok, issued.Message);

        // 10:00 in Algiers on 1 March: booked at the start of 1 March there, still in March
        // on the company's clock though the instant is 28 February in UTC.
        var entry = await h.Db.LedgerEntries.AsNoTracking().SingleAsync(e => e.SourceType == LedgerSources.RentalIssue);
        Assert.Equal(new DateTime(2026, 2, 28, 23, 0, 0, DateTimeKind.Utc), entry.EntryDate);
    }

    [Fact]
    public async Task A_new_cutoff_hour_does_not_move_a_batch_already_out()
    {
        var (h, contractId, _) = await OwnersExampleAsync();
        await using var _h = h;

        // Out at 15:00 under a noon cutoff started on the 27th. Moving the hour to 18:00 would
        // make the same delivery start on the 26th if the day were recomputed; it is frozen.
        Assert.True((await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, 0, DayCutoffHour: 18))).Ok);
        var docs = (await h.Rentals.ReadAsync(contractId))!;
        Assert.Equal(new DateOnly(2026, 9, 27), docs.IssueNotes.Single().PeriodStartsOn);
        Assert.Equal(new DateOnly(2026, 9, 27), h.Rentals.PeriodStartsOn(docs, docs.IssueNotes.Single()));
    }

    [Fact]
    public async Task A_note_raised_before_the_frozen_day_is_frozen_under_the_old_hour_when_the_hour_changes()
    {
        var (h, contractId, _) = await OwnersExampleAsync();
        await using var _h = h;

        // As a note raised before the column existed looks: no frozen day, the noon cutoff in force.
        var note = await h.Db.IssueNotes.SingleAsync();
        note.PeriodStartsOn = null;
        await h.Db.SaveChangesAsync();

        Assert.True((await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, 0))).Ok);
        h.Db.ChangeTracker.Clear();
        Assert.Equal(new DateOnly(2026, 9, 27), (await h.Db.IssueNotes.SingleAsync()).PeriodStartsOn);
    }

    [Fact]
    public async Task Back_on_the_29th_in_the_morning_charges_two_days_and_gives_one_back()
    {
        var (h, contractId, lineId) = await OwnersExampleAsync();
        await using var _h = h;

        // 08:00 UTC is 09:00 in Algiers.
        var back = await h.Rentals.ReturnAsync(contractId, BackAt(lineId, "2026-09-29T08:00:00"), null, "clerk");
        Assert.True(back.Ok, back.Message);

        var rent = back.Value!.Charges.Single(c => c.Kind == Domain.RentalChargeKind.Rent);
        Assert.Equal(2, rent.Days);
        Assert.Equal(200m, rent.Amount);
        Assert.Equal(100m, back.Value.Note.UnusedRent);
        Assert.False(back.Value.Note.CountReturnDay);
        Assert.Null(back.Value.Note.ReturnDayDecidedBy);
    }

    [Fact]
    public async Task Back_on_the_29th_in_the_afternoon_charges_three_days()
    {
        var (h, contractId, lineId) = await OwnersExampleAsync();
        await using var _h = h;

        var back = await h.Rentals.ReturnAsync(contractId, BackAt(lineId, "2026-09-29T15:00:00"), null, "clerk");
        Assert.True(back.Ok, back.Message);

        var rent = back.Value!.Charges.Single(c => c.Kind == Domain.RentalChargeKind.Rent);
        Assert.Equal(3, rent.Days);
        Assert.Equal(300m, rent.Amount);
        Assert.Equal(0m, back.Value.Note.UnusedRent);
        Assert.True(back.Value.Note.CountReturnDay);
    }

    [Fact]
    public async Task The_manager_waives_the_afternoon_only_in_writing_and_it_is_two_days()
    {
        var (h, contractId, lineId) = await OwnersExampleAsync();
        await using var _h = h;

        var unexplained = await h.Rentals.ReturnAsync(contractId, BackAt(lineId, "2026-09-29T15:00:00", countReturnDay: false), null, "Mourad");
        Assert.Equal(RentalFailure.Invalid, unexplained.Failure);
        Assert.Equal(
            "For goods back at 16:00 the rule counts the return day. Not counting it is the branch manager's decision: write the reason for it.",
            unexplained.Message);
        Assert.Empty(await h.Db.ReturnNotes.AsNoTracking().ToListAsync());

        var waived = await h.Rentals.ReturnAsync(
            contractId, BackAt(lineId, "2026-09-29T15:00:00", countReturnDay: false, reason: " Camion bloqué au barrage "), null, "Mourad");
        Assert.True(waived.Ok, waived.Message);
        Assert.Equal(200m, waived.Value!.Charges.Single(c => c.Kind == Domain.RentalChargeKind.Rent).Amount);

        var note = await h.Db.ReturnNotes.AsNoTracking().SingleAsync();
        Assert.False(note.CountReturnDay);
        Assert.Equal("Camion bloqué au barrage", note.ReturnDayReason);
        Assert.Equal("Mourad", note.ReturnDayDecidedBy);

        // Stored, not re-decided: the settlement read back later still says two days.
        Assert.Equal(200m, (await h.Rentals.SettleAsync(contractId))!.Total(Application.Rentals.RentalChargeKind.Rent));
    }

    [Fact]
    public async Task With_no_cutoff_nothing_changes()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 1, days: 3), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 1)], Utc("2026-09-26T14:00:00"))), null, null);

        var back = await h.Rentals.ReturnAsync(contract.Id, BackAt(lineId, "2026-09-29T15:00:00"), null, null);
        Assert.Equal(3, back.Value!.Charges.Single(c => c.Kind == Domain.RentalChargeKind.Rent).Days);
        Assert.False(back.Value.Note.CountReturnDay);
    }

    [Fact]
    public async Task The_cutoff_hour_is_saved_read_back_and_refused_out_of_range()
    {
        await using var h = await NewAsync();

        var refused = await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, 0, DayCutoffHour: 24));
        Assert.Equal(RentalFailure.Invalid, refused.Failure);
        Assert.Contains("between 0 and 23", refused.Message);

        Assert.True((await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, 0, DayCutoffHour: 12))).Ok);
        Assert.Equal(12, (await h.Rentals.SettingsAsync()).DayCutoffHour);

        Assert.True((await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, 0))).Ok);
        Assert.Null((await h.Rentals.SettingsAsync()).DayCutoffHour);
    }

    // ---------- the replacement value and the Arabic name ----------

    [Fact]
    public async Task A_line_starts_from_the_item_s_replacement_value_and_a_typed_one_wins()
    {
        await using var h = await NewAsync();

        var negative = await h.Rentals.SetProductMultiplierAsync(Scaffold, null, null, -1m);
        Assert.Equal(RentalFailure.Invalid, negative.Failure);

        var set = await h.Rentals.SetProductMultiplierAsync(Scaffold, null, null, 3500m);
        Assert.True(set.Ok);
        Assert.Contains(await h.Rentals.ProductMultipliersAsync(), r => r.ProductId == Scaffold && r.RentalDamageUnitPrice == 3500m);

        var blank = await h.Rentals.CreateContractAsync(
            new UpsertRentalContractRequest(Contractor, Algiers,
                [new RentalContractLineRequest(Scaffold, 10, 30, RentUnitPrice: 3000, DamageUnitPrice: 0)]) { SiteId = ContractorSite },
            "admin");
        Assert.Equal(3500m, blank.Value!.Lines.Single().DamageUnitPrice);

        // Contract() types 2,000 on its line, and the figure typed is the figure signed.
        Assert.Equal(2000m, (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!.Lines.Single().DamageUnitPrice);

        // Blank clears it.
        Assert.True((await h.Rentals.SetProductMultiplierAsync(Scaffold, null, null, null)).Ok);
        Assert.Null((await h.Db.Products.AsNoTracking().FirstAsync(p => p.Id == Scaffold)).RentalDamageUnitPrice);
    }

    [Fact]
    public async Task The_Arabic_name_is_frozen_on_the_line_beside_the_French()
    {
        await using var h = await NewAsync();
        var product = await h.Db.Products.FirstAsync(p => p.Id == Scaffold);
        product.NameAr = " سقالة ";
        await h.Db.SaveChangesAsync();

        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        Assert.Equal("سقالة", contract.Lines.Single().NameArSnapshot);
        Assert.Equal("Échafaudage", contract.Lines.Single().NameSnapshot);

        // Renamed later: the signed line keeps what the customer read.
        product.NameAr = "سقالة معدنية";
        await h.Db.SaveChangesAsync();
        Assert.Equal("سقالة", (await h.Db.RentalContractLines.AsNoTracking().SingleAsync()).NameArSnapshot);

        // No Arabic name on the item: nothing is invented.
        var coupler = (await h.Rentals.CreateContractAsync(
            new UpsertRentalContractRequest(Contractor, Algiers,
                [new RentalContractLineRequest(Coupler, 10, 30, RentUnitPrice: 100, DamageUnitPrice: 90)]) { SiteId = ContractorSite },
            "admin")).Value!;
        Assert.Null(coupler.Lines.Single().NameArSnapshot);
    }

    // ---------- the identity scan ----------

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];

    [Fact]
    public async Task The_identity_scan_is_kept_by_what_its_bytes_say_it_is_in_the_files_folder()
    {
        await using var h = await NewAsync();
        var files = TestFiles.Fresh();
        var attachments = new RentalAttachments(h.Db, files);
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;

        var kept = await attachments.AddAsync(contract.Id, "idDocument", @"C:\photos\permis.jpg", Jpeg, "admin");
        Assert.True(kept.Ok, kept.Message);
        Assert.Equal("image/jpeg", kept.Value!.ContentType);
        Assert.Equal("permis.jpg", kept.Value.FileName);
        Assert.Equal(Jpeg.Length, kept.Value.Length);

        // The row keeps where the file is (owner, 2026-10-04); the bytes are only in the folder.
        Assert.Equal(FilePaths.ContractAttachment(contract.Id, kept.Value.Id, "image/jpeg"), kept.Value.StoragePath);
        Assert.Equal(Jpeg, await files.ReadAllAsync(kept.Value.StoragePath));

        var listed = await h.Rentals.AttachmentsAsync(contract.Id);
        Assert.Equal("permis.jpg", Assert.Single(listed).FileName);
        var opened = await attachments.OpenAsync(contract.Id, kept.Value.Id);
        Assert.NotNull(opened);
        await using (var content = opened.Value.Content)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy);
            Assert.Equal(Jpeg, copy.ToArray());
        }
        // Another contract's id does not reach this one's file.
        Assert.Null(await attachments.OpenAsync(Guid.NewGuid(), kept.Value.Id));

        // A text file renamed .jpg is not a picture of anything, and nothing is written for it.
        var text = await attachments.AddAsync(contract.Id, "idDocument", "carte.jpg", "hello"u8.ToArray(), "admin");
        Assert.Equal("Only a JPEG, PNG or WebP image, or a PDF, can be kept with a contract.", text.Message);

        var wrongKind = await attachments.AddAsync(contract.Id, "invoice", "a.jpg", Jpeg, "admin");
        Assert.Equal(RentalFailure.Invalid, wrongKind.Failure);

        var big = new byte[RentalDocuments.MaxAttachmentBytes + 1];
        Jpeg.CopyTo(big, 0);
        var tooBig = await attachments.AddAsync(contract.Id, "idDocument", "big.jpg", big, "admin");
        Assert.StartsWith("The file is larger than 5 MB.", tooBig.Message);

        var pdf = await attachments.AddAsync(contract.Id, "idDocument", "scan.pdf", "%PDF-1.7\n"u8.ToArray(), "admin");
        Assert.Equal("application/pdf", pdf.Value!.ContentType);
        Assert.EndsWith(".pdf", pdf.Value.StoragePath);
        var png = await attachments.AddAsync(contract.Id, "idDocument", "scan.png",
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00], "admin");
        Assert.Equal("image/png", png.Value!.ContentType);
        var webp = await attachments.AddAsync(contract.Id, "idDocument", "scan.webp",
            "RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), "admin");
        Assert.Equal("image/webp", webp.Value!.ContentType);
    }

    [Fact]
    public async Task The_identity_scan_can_be_removed_with_its_file_before_the_lock_and_never_after()
    {
        await using var h = await NewAsync();
        var files = TestFiles.Fresh();
        var attachments = new RentalAttachments(h.Db, files);
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var first = (await attachments.AddAsync(contract.Id, "idDocument", "a.jpg", Jpeg, "admin")).Value!;
        var second = (await attachments.AddAsync(contract.Id, "idDocument", "b.jpg", Jpeg, "admin")).Value!;

        Assert.True((await attachments.RemoveAsync(contract.Id, first.Id)).Ok);
        Assert.Null(await files.ReadAllAsync(first.StoragePath));
        Assert.Equal(RentalFailure.NotFound, (await attachments.RemoveAsync(contract.Id, first.Id)).Failure);

        await h.Rentals.IssueAsync(contract.Id, Issue(contract.Lines.Single().Id), null, null);
        var locked = await attachments.RemoveAsync(contract.Id, second.Id);
        Assert.Equal(RentalFailure.Conflict, locked.Failure);
        Assert.Single(await h.Rentals.AttachmentsAsync(contract.Id));
        Assert.Equal(Jpeg, await files.ReadAllAsync(second.StoragePath));
    }
}
