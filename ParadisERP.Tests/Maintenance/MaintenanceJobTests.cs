using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Maintenance;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Maintenance.MaintenanceWorld;

namespace ParadisERP.Tests.Maintenance;

/// <summary>
/// Maintenance jobs, Phase M1 (docs/maintenance/plan.md §2.2, §8 row M1): units already in maintenance taken by a job,
/// moved by its completion exactly as the manual state route moves them, never more than the bucket holds; the settings
/// MS-01, MS-02, MS-03 and MS-05; branch scope.
/// </summary>
public class MaintenanceJobTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Json(object o) => JsonSerializer.Serialize(o, Web);
    private static string? CodeOf(string body) =>
        JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;

    /// <summary>The movements and ledger lines a world wrote, in the shape both paths must share.</summary>
    private static async Task<(List<string> Movements, List<string> Ledger)> TraceAsync(PosDbContext db)
    {
        var movements = (await db.StockMovements.AsNoTracking().Where(m => m.ProductId == Scaffold).ToListAsync())
            .OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Reason)
            .Select(m => $"{m.BranchId}|{m.Kind}|{m.Reason}|{m.QtyDelta:0.###}|{m.MovedQty:0.###}|{m.BalanceAfter:0.###}|{m.UnitCost:0.###}|{m.UserName}")
            .ToList();
        var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var entries = await db.LedgerEntries.AsNoTracking().Include(e => e.Lines).Where(e => e.SourceType == LedgerSources.StockState).ToListAsync();
        var ledger = entries.SelectMany(e => e.Lines.Select(l => $"{e.BranchId}|{accounts[l.AccountId]}|{l.Debit:0.###}|{l.Credit:0.###}|{l.Memo}"))
            .Order(StringComparer.Ordinal).ToList();
        return (movements, ledger);
    }

    // ---------- the units move exactly as the manual route moves them ----------

    [Fact]
    public async Task Completing_moves_the_units_with_the_same_movements_and_the_same_318_310_entry_as_the_manual_route()
    {
        // Two identical companies with ten scaffolds in maintenance. One moves 6 back and 4 to scrap by hand; the other
        // opens a job for the ten and completes it 6 repaired, 4 beyond repair, over the route.
        await using var byHand = await NewAsync(On(), inMaintenance: 10);
        await using var byJob = await NewAsync(On(), inMaintenance: 10);
        var handCaller = await byHand.CallerAsync(UserRole.Admin, Algiers, Chef);
        Assert.Equal(200, (await byHand.StateAsync(handCaller, StockState.Maintenance, StockState.Available, 6)).Status);
        Assert.Equal(200, (await byHand.StateAsync(handCaller, StockState.Maintenance, StockState.Scrap, 4)).Status);

        var jobCaller = await byJob.CallerAsync(UserRole.Admin, Algiers, Chef);
        var job = await byJob.OpenAsync(Internal(10), Chef);
        Assert.Equal(("Open", "MJ-000001"), (job.Status, job.Number));
        // Opening moves nothing.
        Assert.Equal((100m, 10m, 0m), ((await byJob.StockAsync(Algiers)).StockQty, (await byJob.StockAsync(Algiers)).MaintenanceQty, (await byJob.StockAsync(Algiers)).ScrapQty));
        Assert.Empty(await byJob.Db.StockMovements.AsNoTracking().ToListAsync());

        var done = await byJob.CallAsync(jobCaller, "POST", "/api/maintenance/jobs/{id:guid}/complete",
            Json(new CompleteMaintenanceJobRequest(6, 4, "Soudure de deux cadres")), ("id", job.Id.ToString()));
        Assert.Equal(200, done.Status);
        var completed = JsonSerializer.Deserialize<MaintenanceJobDto>(done.Body, Web)!;
        Assert.Equal(("Completed", 6m, 4m, "chef"), (completed.Status, completed.RepairedQty, completed.ScrappedQty, completed.CompletedBy));

        var a = await byHand.StockAsync(Algiers);
        var b = await byJob.StockAsync(Algiers);
        Assert.Equal((106m, 0m, 4m), (b.StockQty, b.MaintenanceQty, b.ScrapQty));
        Assert.Equal((a.StockQty, a.OnRentQty, a.MaintenanceQty, a.ScrapQty), (b.StockQty, b.OnRentQty, b.MaintenanceQty, b.ScrapQty));

        var (handMoves, handLedger) = await TraceAsync(byHand.Db);
        var (jobMoves, jobLedger) = await TraceAsync(byJob.Db);
        Assert.Equal(handMoves, jobMoves);
        Assert.Equal(handLedger, jobLedger);
        // Scrap is Dr 318 / Cr 310 at cost; back to available posts nothing.
        Assert.Equal(2, jobLedger.Count);
        Assert.Contains($"{Algiers}|{LedgerAccounts.ScrapInventory}|{4 * Cost:0.###}|0|ECH-01", jobLedger);
        Assert.Contains($"{Algiers}|{LedgerAccounts.Inventory}|0|{4 * Cost:0.###}|ECH-01", jobLedger);
        // The job's movements carry its number; the manual route's have none to carry.
        Assert.All(await byJob.Db.StockMovements.AsNoTracking().ToListAsync(), m => Assert.Equal("MJ-000001", m.DocumentNumber));
        Assert.All(await byHand.Db.StockMovements.AsNoTracking().ToListAsync(), m => Assert.Null(m.DocumentNumber));
        Assert.Contains("MJ-000001", (await byJob.Db.LedgerEntries.AsNoTracking().SingleAsync()).Description);

        var events = (await byJob.Jobs.GetAsync(job.Id)).Value!.Events.Select(e => e.Kind);
        Assert.Equal(["Opened", "Completed"], events);
    }

    [Fact]
    public async Task Completion_accounts_for_every_unit_and_a_job_is_completed_once()
    {
        await using var w = await NewAsync(On(), inMaintenance: 5);
        var job = await w.OpenAsync(Internal(5));
        var partial = await w.Jobs.CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(3, 1, null), Chef);
        Assert.Equal((400, "invalid_quantity"), (partial.Status, partial.Code));
        Assert.Equal(5m, (await w.StockAsync(Algiers)).MaintenanceQty);

        Assert.True((await w.Jobs.CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(5, 0, null), Chef)).Ok);
        var again = await w.Jobs.CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(5, 0, null), Chef);
        Assert.Equal((409, "not_live"), (again.Status, again.Code));
        Assert.Equal((105m, 0m), ((await w.StockAsync(Algiers)).StockQty, (await w.StockAsync(Algiers)).MaintenanceQty));
        Assert.Single(await w.Db.StockMovements.AsNoTracking().ToListAsync());
    }

    // ---------- never more than the bucket holds ----------

    [Fact]
    public async Task A_job_takes_no_more_than_is_in_maintenance_less_what_open_jobs_hold_and_cancelling_frees_it()
    {
        await using var w = await NewAsync(On(), inMaintenance: 5);
        var first = await w.OpenAsync(Internal(3));

        var tooMany = await w.Jobs.CreateAsync(Internal(3), Chef);
        Assert.Equal((409, MaintenanceJobs.ExceedsMaintenanceCode), (tooMany.Status, tooMany.Code));
        Assert.Contains("already hold 3", tooMany.Error);
        // Nothing in maintenance at Oran.
        Assert.Equal(409, (await w.Jobs.CreateAsync(Internal(1, Oran), Chef)).Status);

        var second = await w.OpenAsync(Internal(2));
        Assert.Equal("MJ-000002", second.Number);
        Assert.Equal(409, (await w.Jobs.CreateAsync(Internal(1), Chef)).Status);

        // Cancelling moves nothing: the units stay in maintenance, free for another job.
        var cancelled = await w.Jobs.CancelAsync(first.Id, new CancelMaintenanceJobRequest("Ouvert par erreur"), Chef);
        Assert.Equal(("Cancelled", "Ouvert par erreur"), (cancelled.Value!.Status, cancelled.Value.CancelReason));
        Assert.Equal((100m, 5m), ((await w.StockAsync(Algiers)).StockQty, (await w.StockAsync(Algiers)).MaintenanceQty));
        Assert.Empty(await w.Db.StockMovements.AsNoTracking().ToListAsync());
        Assert.True((await w.Jobs.CreateAsync(Internal(3), Chef)).Ok);

        // A cancelled job is closed; a reason is required to cancel.
        Assert.Equal(409, (await w.Jobs.CompleteAsync(first.Id, new CompleteMaintenanceJobRequest(3, 0, null), Chef)).Status);
        Assert.Equal((400, "reason_required"), (await w.Jobs.CancelAsync(second.Id, new CancelMaintenanceJobRequest(" "), Chef)) is var r ? (r.Status, r.Code) : default);
    }

    [Fact]
    public async Task A_retried_request_answers_the_job_it_opened_and_a_customer_job_waits_for_M5()
    {
        await using var w = await NewAsync(On(), inMaintenance: 5);
        var ask = Internal(2);
        var one = await w.OpenAsync(ask);
        var retry = await w.OpenAsync(ask);
        Assert.Equal(one.Id, retry.Id);
        Assert.Equal(1, await w.Db.MaintenanceJobs.CountAsync());

        var customer = await w.Jobs.CreateAsync(ask with { ClientRequestId = "c-1", Kind = "Customer" }, Chef);
        Assert.Equal((400, MaintenanceJobs.NotYetCode), (customer.Status, customer.Code));
        Assert.Equal(400, (await w.Jobs.CreateAsync(ask with { ClientRequestId = "c-2", Kind = "Whatever" }, Chef)).Status);
        Assert.Equal(400, (await w.Jobs.CreateAsync(ask with { ClientRequestId = "c-3", Qty = 0 }, Chef)).Status);
    }

    [Fact]
    public async Task Two_completions_of_one_job_at_the_same_moment_move_its_units_once()
    {
        await using var seed = await NewAsync(On(), inMaintenance: 4);
        var job = await seed.OpenAsync(Internal(4));
        var database = Path.Combine(Path.GetTempPath(), $"algeriapos-mj-{Guid.NewGuid():N}.db");
        try
        {
            await using (var copy = new SqliteConnection($"Data Source={database};Cache=Shared"))
            {
                await copy.OpenAsync();
                seed.Connection.BackupDatabase(copy);
            }
            var options = new DbContextOptionsBuilder<PosDbContext>().UseSqlite($"Data Source={database};Cache=Shared").Options;
            await using var first = new PosDbContext(options);
            await using var second = new PosDbContext(options);
            await first.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
            await second.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");

            var replies = await Task.WhenAll(
                Task.Run(() => new MaintenanceJobs(first, new LedgerPoster(first), MaintenanceWorld.Regional).CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(1, 3, null), Chef)),
                Task.Run(() => new MaintenanceJobs(second, new LedgerPoster(second), MaintenanceWorld.Regional).CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(1, 3, null), Second)));

            Assert.Single(replies, r => r.Ok);
            Assert.Single(replies, r => r.Status == 409);
            await using var look = new PosDbContext(options);
            var stock = await look.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == Scaffold && s.BranchId == Algiers);
            Assert.Equal((101m, 0m, 3m), (stock.StockQty, stock.MaintenanceQty, stock.ScrapQty));
            Assert.Equal(2, await look.StockMovements.CountAsync());
            Assert.Equal(1, await look.LedgerEntries.CountAsync(e => e.SourceType == LedgerSources.StockState));
            Assert.Equal(1, await look.MaintenanceJobEvents.CountAsync(e => e.JobId == job.Id && e.Kind == MaintenanceJobEventKind.Completed));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [Fact]
    public async Task Completion_is_refused_when_the_units_have_left_maintenance_by_hand_and_nothing_moves()
    {
        // MS-02's default leaves the manual route as it is: someone moves units out from under an open job.
        await using var w = await NewAsync(On(), inMaintenance: 3);
        var job = await w.OpenAsync(Internal(3));
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Second);
        Assert.Equal(200, (await w.StateAsync(admin, StockState.Maintenance, StockState.Available, 2)).Status);

        var refused = await w.Jobs.CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(2, 1, null), Chef);
        Assert.Equal((409, "stock_state"), (refused.Status, refused.Code));
        Assert.Equal((102m, 1m, 0m), ((await w.StockAsync(Algiers)).StockQty, (await w.StockAsync(Algiers)).MaintenanceQty, (await w.StockAsync(Algiers)).ScrapQty));
        Assert.Equal("Open", (await w.Jobs.GetAsync(job.Id)).Value!.Status);
    }

    // ---------- recharged jobs and the return note's lines ----------

    [Fact]
    public async Task A_return_note_line_is_waiting_until_jobs_cover_its_damaged_units_and_never_beyond_them()
    {
        await using var w = await NewAsync(On());
        var (contractId, noteId, lineId, _) = await w.ReturnDamagedAsync(damaged: 20);
        Assert.Equal(20m, (await w.StockAsync(Algiers)).MaintenanceQty);

        var waiting = await w.Jobs.WaitingAsync(null);
        var line = Assert.Single(waiting.Lines);
        Assert.Equal((lineId, noteId, contractId, 20m, 0m, 20m, 15_000m, "Ben Ali BTP"),
            (line.ReturnNoteLineId, line.ReturnNoteId, line.ContractId, line.DamagedQty, line.CoveredQty, line.OpenQty, line.Recharge, line.CustomerName));
        var bucket = Assert.Single(waiting.Buckets);
        Assert.Equal((20m, 0m, 20m), (bucket.InMaintenance, bucket.HeldByJobs, bucket.Free));

        var first = await w.OpenAsync(Recharged(lineId, 12));
        Assert.Equal(("Recharged", contractId, noteId, lineId, Scaffold), (first.Kind, first.ContractId, first.ReturnNoteId, first.ReturnNoteLineId, first.ProductId));
        Assert.Equal((20m, 15_000m), (first.LineDamagedQty!.Value, first.LineRecharge!.Value));
        line = Assert.Single((await w.Jobs.WaitingAsync(Algiers)).Lines);
        Assert.Equal((12m, 8m), (line.CoveredQty, line.OpenQty));

        var beyond = await w.Jobs.CreateAsync(Recharged(lineId, 9), Chef);
        Assert.Equal((409, MaintenanceJobs.LineCoveredCode), (beyond.Status, beyond.Code));
        var rest = await w.OpenAsync(Recharged(lineId, 8));
        Assert.Empty((await w.Jobs.WaitingAsync(null)).Lines);
        // The bucket is all held: an internal job finds nothing free.
        Assert.Equal(409, (await w.Jobs.CreateAsync(Internal(1), Chef)).Status);
        Assert.Equal((20m, 20m, 0m), Assert.Single((await w.Jobs.WaitingAsync(null)).Buckets) is var b2 ? (b2.InMaintenance, b2.HeldByJobs, b2.Free) : default);

        // A completed job still covers its units; a cancelled one gives them back to the waiting list.
        Assert.True((await w.Jobs.CompleteAsync(first.Id, new CompleteMaintenanceJobRequest(12, 0, null), Chef)).Ok);
        Assert.Empty((await w.Jobs.WaitingAsync(null)).Lines);
        Assert.True((await w.Jobs.CancelAsync(rest.Id, new CancelMaintenanceJobRequest("Sous-traité"), Chef)).Ok);
        line = Assert.Single((await w.Jobs.WaitingAsync(null)).Lines);
        Assert.Equal((12m, 8m), (line.CoveredQty, line.OpenQty));

        // Another branch's line is not found from Oran, and an internal job names no line.
        Assert.Equal(404, (await w.Jobs.CreateAsync(Recharged(lineId, 1, Oran), Chef)).Status);
        Assert.Equal(400, (await w.Jobs.CreateAsync(Internal(1) with { ReturnNoteLineId = lineId }, Chef)).Status);
        // Waiting for repair at Oran shows nothing of Algiers.
        var oran = await w.Jobs.WaitingAsync(Oran);
        Assert.Empty(oran.Lines);
        Assert.Empty(oran.Buckets);
    }

    // ---------- MS-01: the module off ----------

    [Fact]
    public async Task With_the_module_off_every_route_but_the_settings_answers_409_maintenance_off()
    {
        await using var w = await NewAsync(inMaintenance: 3);
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        var id = ("id", Guid.NewGuid().ToString());
        foreach (var (method, pattern, body) in new[]
        {
            ("GET", "/api/maintenance/jobs", ""), ("GET", "/api/maintenance/waiting", ""), ("GET", "/api/maintenance/jobs/{id:guid}", ""),
            ("POST", "/api/maintenance/jobs", Json(Internal(1))), ("PUT", "/api/maintenance/jobs/{id:guid}", Json(new UpdateMaintenanceJobRequest(null, "x", null))),
            ("POST", "/api/maintenance/jobs/{id:guid}/open", ""), ("POST", "/api/maintenance/jobs/{id:guid}/complete", Json(new CompleteMaintenanceJobRequest(1, 0, null))),
            ("POST", "/api/maintenance/jobs/{id:guid}/cancel", Json(new CancelMaintenanceJobRequest("x"))),
        })
        {
            var (status, answer) = await w.CallAsync(admin, method, pattern, body, id);
            Assert.True(status == 409, $"{method} {pattern} answered {status}");
            Assert.Equal(MaintenanceJobs.OffCode, CodeOf(answer));
        }
        Assert.Equal(0, await w.Db.MaintenanceJobs.CountAsync());
        Assert.Equal(200, (await w.CallAsync(admin, "GET", "/api/maintenance/settings")).Status);

        // On, the same list answers.
        w.Db.MaintenanceSettings.Add(new MaintenanceSettings { Module = MaintenanceSwitch.On });
        await w.Db.SaveChangesAsync();
        Assert.Equal(200, (await w.CallAsync(admin, "GET", "/api/maintenance/jobs")).Status);
        var created = await w.CallAsync(admin, "POST", "/api/maintenance/jobs", Json(Internal(1)));
        Assert.Equal(201, created.Status);
    }

    // ---------- MS-02: units leave maintenance only by a job ----------

    [Fact]
    public async Task Only_by_a_job_refuses_the_manual_maintenance_to_available_and_the_default_leaves_the_route_as_it_was()
    {
        await using var onlyByJob = await NewAsync(On(s => s.UnitsLeaveMaintenance = MaintenanceUnitRelease.OnlyByJob), inMaintenance: 5);
        var admin = await onlyByJob.CallerAsync(UserRole.Admin, Algiers, Chef);
        var refused = await onlyByJob.StateAsync(admin, StockState.Maintenance, StockState.Available, 1);
        Assert.Equal(409, refused.Status);
        Assert.Equal(ParadisERP.API.Endpoints.StockEndpoints.MaintenanceOnlyByJobCode, CodeOf(refused.Body));
        Assert.Equal(5m, (await onlyByJob.StockAsync(Algiers)).MaintenanceQty);
        // Only that direction: into maintenance and on to scrap stay by hand.
        Assert.Equal(200, (await onlyByJob.StateAsync(admin, StockState.Available, StockState.Maintenance, 1)).Status);
        Assert.Equal(200, (await onlyByJob.StateAsync(admin, StockState.Maintenance, StockState.Scrap, 1)).Status);
        // And a job still takes them back to available.
        var job = await onlyByJob.OpenAsync(Internal(2));
        Assert.True((await onlyByJob.Jobs.CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(2, 0, null), Chef)).Ok);
        Assert.Equal((101m, 3m, 1m), ((await onlyByJob.StockAsync(Algiers)).StockQty, (await onlyByJob.StockAsync(Algiers)).MaintenanceQty, (await onlyByJob.StockAsync(Algiers)).ScrapQty));

        // The default, and "only by a job" with the module off, leave the route as it always was.
        foreach (var settings in new Action<MaintenanceSettings>?[] { null, On(), s => s.UnitsLeaveMaintenance = MaintenanceUnitRelease.OnlyByJob })
        {
            await using var w = await NewAsync(settings, inMaintenance: 5);
            var caller = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
            Assert.Equal(200, (await w.StateAsync(caller, StockState.Maintenance, StockState.Available, 2, "Réparé")).Status);
            var stock = await w.StockAsync(Algiers);
            Assert.Equal((102m, 3m), (stock.StockQty, stock.MaintenanceQty));
            var moved = await w.Db.StockMovements.AsNoTracking().SingleAsync();
            Assert.Equal(("maintenance->available: réparé", StockMovementKind.StateChange, 0m, 2m, Cost, (string?)null),
                (moved.Reason, moved.Kind, moved.QtyDelta, moved.MovedQty!.Value, moved.UnitCost!.Value, moved.DocumentNumber));
        }
    }

    // ---------- MS-03: draft jobs from the return note ----------

    [Fact]
    public async Task A_draft_job_per_damaged_line_opens_in_the_return_note_s_transaction_only_when_chosen_and_the_rental_is_the_same_either_way()
    {
        await using var byDefault = await NewAsync();
        await using var offeredInList = await NewAsync(On());
        await using var drafts = await NewAsync(On(s => s.JobsFromReturnNotes = MaintenanceReturnNoteJobs.DraftJobPerDamagedLine));
        await using var draftsButOff = await NewAsync(s => s.JobsFromReturnNotes = MaintenanceReturnNoteJobs.DraftJobPerDamagedLine);

        var rentals = new List<string>();
        foreach (var w in new[] { byDefault, offeredInList, drafts, draftsButOff })
        {
            var (_, _, _, outcome) = await w.ReturnDamagedAsync(damaged: 20, recharge: 16_000);
            var stock = await w.StockAsync(Algiers);
            var charges = string.Join(";", outcome.Charges.OrderBy(c => c.Kind).Select(c => $"{c.Kind}:{c.Amount:0.##}:{c.IsPosted}"));
            var (movements, _) = await TraceAsync(w.Db);
            var accounts = await w.Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
            var ledger = (await w.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines).ToListAsync())
                .SelectMany(e => e.Lines.Select(l => $"{e.SourceType}|{accounts[l.AccountId]}|{l.Debit:0.###}|{l.Credit:0.###}")).Order(StringComparer.Ordinal);
            rentals.Add($"{outcome.Note.Number}|{stock.StockQty}|{stock.MaintenanceQty}|{stock.OnRentQty}|{charges}|{string.Join(",", movements.Select(m => m[(m.IndexOf('|') + 1)..]))}|{string.Join(",", ledger)}");
        }
        // The rental — note, stock, charges, movements, ledger — is the same in all four companies.
        Assert.Single(rentals.Distinct());

        Assert.Equal(0, await byDefault.Db.MaintenanceJobs.CountAsync());
        Assert.Equal(0, await offeredInList.Db.MaintenanceJobs.CountAsync());
        Assert.Equal(0, await draftsButOff.Db.MaintenanceJobs.CountAsync());
        var draft = await drafts.Db.MaintenanceJobs.AsNoTracking().SingleAsync();
        var note = await drafts.Db.ReturnNotes.AsNoTracking().Include(n => n.Lines).SingleAsync();
        Assert.Equal((MaintenanceJobKind.Recharged, MaintenanceJobStatus.Draft, 20m, Algiers, Scaffold, note.Id, note.Lines.Single().Id, note.ContractId, "storeman", (Guid?)null),
            (draft.Kind, draft.Status, draft.Qty, draft.BranchId, draft.ProductId, draft.ReturnNoteId, draft.ReturnNoteLineId, draft.ContractId, draft.CreatedBy, draft.OpenedByUserId));
        Assert.Equal("Drafted", Assert.Single((await drafts.Jobs.GetAsync(draft.Id)).Value!.Events).Kind);
        // The draft covers the line and holds the units; it is opened by whoever takes it, then completed.
        Assert.Empty((await drafts.Jobs.WaitingAsync(null)).Lines);
        Assert.Equal((409, "not_open"), (await drafts.Jobs.CompleteAsync(draft.Id, new CompleteMaintenanceJobRequest(20, 0, null), Chef)) is var early ? (early.Status, early.Code) : default);
        var opened = await drafts.Jobs.OpenAsync(draft.Id, Second);
        Assert.Equal(("Open", "adjoint"), (opened.Value!.Status, opened.Value.OpenedBy));
        Assert.Equal(409, (await drafts.Jobs.OpenAsync(draft.Id, Second)).Status);
        Assert.True((await drafts.Jobs.CompleteAsync(draft.Id, new CompleteMaintenanceJobRequest(18, 2, null), Chef)).Ok);
        var after = await drafts.StockAsync(Algiers);
        Assert.Equal((98m, 0m, 2m), (after.StockQty, after.MaintenanceQty, after.ScrapQty));
    }

    // ---------- MS-05: who closes ----------

    [Fact]
    public async Task Under_four_eyes_the_opener_cannot_complete_and_by_default_they_can()
    {
        await using var fourEyes = await NewAsync(On(s => s.JobCloser = MaintenanceJobCloser.NotTheOpener), inMaintenance: 2);
        var job = await fourEyes.OpenAsync(Internal(2), Chef);
        Assert.Equal("NotTheOpener", job.JobCloser);
        var own = await fourEyes.Jobs.CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(2, 0, null), Chef);
        Assert.Equal((403, MaintenanceJobs.OwnJobCode), (own.Status, own.Code));
        Assert.Equal(2m, (await fourEyes.StockAsync(Algiers)).MaintenanceQty);
        Assert.True((await fourEyes.Jobs.CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(2, 0, null), Second)).Ok);

        await using var anyHolder = await NewAsync(On(), inMaintenance: 2);
        var mine = await anyHolder.OpenAsync(Internal(2), Chef);
        Assert.True((await anyHolder.Jobs.CompleteAsync(mine.Id, new CompleteMaintenanceJobRequest(2, 0, null), Chef)).Ok);
    }

    // ---------- branch scope ----------

    [Fact]
    public async Task A_branch_user_never_reaches_another_branch_s_job_it_answers_404_and_their_list_is_their_branch()
    {
        await using var w = await NewAsync(On(), inMaintenance: 4);
        var algiersJob = await w.OpenAsync(Internal(2));
        var oranManager = await w.CallerAsync(UserRole.Manager, Oran, Second);
        var algiersManager = await w.CallerAsync(UserRole.Manager, Algiers, Chef);
        var id = ("id", algiersJob.Id.ToString());

        Assert.Equal(404, (await w.CallAsync(oranManager, "GET", "/api/maintenance/jobs/{id:guid}", "", id)).Status);
        Assert.Equal(404, (await w.CallAsync(oranManager, "POST", "/api/maintenance/jobs/{id:guid}/complete", Json(new CompleteMaintenanceJobRequest(2, 0, null)), id)).Status);
        Assert.Equal(404, (await w.CallAsync(oranManager, "POST", "/api/maintenance/jobs/{id:guid}/cancel", Json(new CancelMaintenanceJobRequest("x")), id)).Status);
        Assert.Equal(404, (await w.CallAsync(oranManager, "PUT", "/api/maintenance/jobs/{id:guid}", Json(new UpdateMaintenanceJobRequest(null, "x", null)), id)).Status);
        Assert.Equal("Open", (await w.Jobs.GetAsync(algiersJob.Id)).Value!.Status);

        // "All branches" or Algiers asked for by an Oran manager reads Oran: nothing.
        var list = await w.CallAsync(oranManager, "GET", "/api/maintenance/jobs", $"?branchId={Algiers}");
        Assert.Equal("[]", list.Body);
        // A job an Oran manager opens naming Algiers is opened at Oran — where nothing is in maintenance.
        var foreign = await w.CallAsync(oranManager, "POST", "/api/maintenance/jobs", Json(Internal(1, Algiers)));
        Assert.Equal(409, foreign.Status);

        Assert.Equal(200, (await w.CallAsync(algiersManager, "GET", "/api/maintenance/jobs/{id:guid}", "", id)).Status);
        var mine = JsonSerializer.Deserialize<List<MaintenanceJobSummaryDto>>((await w.CallAsync(algiersManager, "GET", "/api/maintenance/jobs")).Body, Web)!;
        Assert.Equal(algiersJob.Id, Assert.Single(mine).Id);
    }

    [Fact]
    public async Task Editing_keeps_the_words_of_a_live_job_and_logs_each_change()
    {
        await using var w = await NewAsync(On(), inMaintenance: 1);
        var job = await w.OpenAsync(Internal(1));
        var edited = await w.Jobs.UpdateAsync(job.Id, new UpdateMaintenanceJobRequest("SN-778", "Plancher fendu", "Remplacé la planche"), Second);
        Assert.Equal(("SN-778", "Plancher fendu", "Remplacé la planche"), (edited.Value!.ItemReference, edited.Value.Description, edited.Value.Diagnosis));
        Assert.Equal(["Opened", "Edited"], edited.Value.Events.Select(e => e.Kind));
        Assert.True((await w.Jobs.CompleteAsync(job.Id, new CompleteMaintenanceJobRequest(1, 0, null), Second)).Ok);
        Assert.Equal(409, (await w.Jobs.UpdateAsync(job.Id, new UpdateMaintenanceJobRequest(null, null, null), Second)).Status);
        Assert.Equal("Remplacé la planche", (await w.Jobs.GetAsync(job.Id)).Value!.Diagnosis);
    }
}
