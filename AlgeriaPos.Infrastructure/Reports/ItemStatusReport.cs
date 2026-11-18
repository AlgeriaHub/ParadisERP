using System.Globalization;
using System.Text.RegularExpressions;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Rentals;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Reports;

/// <summary>Who is asking for the item movement &amp; status report.</summary>
public sealed record ItemStatusViewer(Guid? OwnBranch, bool OthersAvailable);

/// <summary>Filter criteria for the item movement &amp; status report.</summary>
public sealed record ItemStatusFilter(
    Guid? LocationId,
    string? Search,
    IReadOnlyCollection<Guid>? ProductIds,
    string? ItemType,
    string? Status,
    Guid? CustomerId,
    Guid? ContractId,
    bool IncludeInactiveItems);

/// <summary>
/// Server read side of the Item Movement &amp; Status report.
/// Quantities come from ProductStock buckets and the rental engine's FIFO match.
/// </summary>
public sealed class ItemStatusReport(PosDbContext db, RentalDocuments rentals, IRegionalSettingsProvider regionalSettings)
{
    private static readonly Regex StateTransitionRegex = new(@"^(\w+)->(\w+)(?::\s*(.*))?$", RegexOptions.Compiled);

    private TimeZoneInfo Zone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(regionalSettings.Get().TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Builds the high-level status summary across catalog products for the viewer's scope.
    /// Location scoping confines branch managers to their own branch in full; other branches are
    /// included strictly as available quantity when granted stock.view_other_branches.
    /// No customer or contract information from other branches ever leaves the server.
    /// </summary>
    public async Task<ItemStatusReportDto> SummaryAsync(ItemStatusViewer viewer, ItemStatusFilter f, CancellationToken ct)
    {
        var branches = await db.Branches.AsNoTracking().Where(b => !b.IsDeleted).ToListAsync(ct);
        var branchMap = branches.ToDictionary(b => b.Id);

        var (fullBranches, otherBranches, scope) = ResolveLocations(branches, viewer, f);
        var fullBranchIds = fullBranches.Select(b => b.Id).ToHashSet();
        var otherBranchIds = otherBranches.Select(b => b.Id).ToHashSet();
        var targetBranchIds = fullBranchIds.Concat(otherBranchIds).ToHashSet();

        var query = db.Products.AsNoTracking().Where(p => !p.IsDeleted);
        if (!f.IncludeInactiveItems) query = query.Where(p => p.IsActive);
        if (string.Equals(f.ItemType, "rental", StringComparison.OrdinalIgnoreCase))
            query = query.Where(p => p.IsRentable);
        else if (string.Equals(f.ItemType, "sale", StringComparison.OrdinalIgnoreCase))
            query = query.Where(p => !p.IsRentable);

        if (f.ProductIds != null && f.ProductIds.Count > 0)
            query = query.Where(p => f.ProductIds.Contains(p.Id));

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            query = query.Where(p =>
                p.Sku.Contains(term) || (p.Barcode != null && p.Barcode.Contains(term)) ||
                p.NameAr.Contains(term) || p.NameFr.Contains(term) || p.NameEn.Contains(term));
        }

        query = query.OrderBy(p => p.Sku);
        var products = await query.ToListAsync(ct);

        var allOutstanding = await rentals.OutstandingAsync(null, ct);
        var outstandingInFull = allOutstanding.Where(b => fullBranchIds.Contains(b.IssueBranchId)).ToList();

        if (f.CustomerId.HasValue || f.ContractId.HasValue)
        {
            var contractIds = outstandingInFull.Select(b => b.ContractId).Distinct().ToList();
            var contractCustomers = await db.RentalContracts.AsNoTracking()
                .Where(c => contractIds.Contains(c.Id))
                .Select(c => new { c.Id, c.CustomerId })
                .ToDictionaryAsync(c => c.Id, c => c.CustomerId, ct);

            var matchingProductIds = outstandingInFull.Where(b =>
                (!f.ContractId.HasValue || b.ContractId == f.ContractId.Value) &&
                (!f.CustomerId.HasValue || contractCustomers.GetValueOrDefault(b.ContractId) == f.CustomerId.Value))
                .Select(b => b.Line.ProductId)
                .ToHashSet();

            products = products.Where(p => matchingProductIds.Contains(p.Id)).ToList();
        }

        if (products.Count == 0)
        {
            var emptyTotals = new ItemStatusTotalsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, otherBranches.Count > 0 ? 0 : null, 0);
            return new ItemStatusReportDto(DateTime.UtcNow, scope, [], emptyTotals);
        }

        var productIds = products.Select(p => p.Id).ToHashSet();

        var stocks = await db.ProductStocks.AsNoTracking()
            .Where(s => targetBranchIds.Contains(s.BranchId) && productIds.Contains(s.ProductId))
            .ToListAsync(ct);

        var outstandingByProduct = outstandingInFull.GroupBy(b => b.Line.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Qty));

        var openContracts = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Status == RentalContractStatus.Open && fullBranchIds.Contains(c.BranchId))
            .Select(c => new { c.Id, c.CustomerId })
            .ToListAsync(ct);

        Dictionary<Guid, decimal> leftToIssueByProduct = [];
        if (openContracts.Count > 0)
        {
            var openContractIds = openContracts.Select(c => c.Id).ToList();
            var openLines = await db.RentalContractLines.AsNoTracking()
                .Where(l => openContractIds.Contains(l.ContractId))
                .Select(l => new { l.Id, l.ContractId, l.ProductId, l.Qty })
                .ToListAsync(ct);
            var openLineIds = openLines.Select(l => l.Id).ToList();
            var issueLines = await db.IssueNoteLines.AsNoTracking()
                .Where(l => openLineIds.Contains(l.ContractLineId))
                .Select(l => new { l.ContractLineId, l.Qty })
                .ToListAsync(ct);
            var issuedByLine = issueLines.GroupBy(x => x.ContractLineId).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
            leftToIssueByProduct = openLines.GroupBy(l => l.ProductId)
                .ToDictionary(g => g.Key, g => g.Sum(l => Math.Max(0m, l.Qty - issuedByLine.GetValueOrDefault(l.Id, 0m))));
        }

        var stocksByProduct = stocks.GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        // In transit counts at the location that dispatched it (StockTransfer): only sources seen in full.
        var transitByProduct = (await OpenTransferLinesAsync(productIds, ct))
            .Where(l => fullBranchIds.Contains(l.Transfer!.FromBranchId))
            .GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.InTransitQty));

        var rows = new List<ItemStatusRowDto>();
        foreach (var p in products)
        {
            var pStocks = stocksByProduct.GetValueOrDefault(p.Id) ?? [];
            var inFull = pStocks.Where(s => fullBranchIds.Contains(s.BranchId)).ToList();
            var inOthers = pStocks.Where(s => otherBranchIds.Contains(s.BranchId)).ToList();
            var stillOut = outstandingByProduct.GetValueOrDefault(p.Id, 0m);
            var leftToIssue = leftToIssueByProduct.GetValueOrDefault(p.Id, 0m);

            var row = ComputeRow(p, inFull, inOthers, branchMap, stillOut, leftToIssue, otherBranches.Count > 0,
                transitByProduct.GetValueOrDefault(p.Id, 0m));
            if (MatchesStatusFilter(row, f.Status))
            {
                rows.Add(row);
            }
        }

        var totals = new ItemStatusTotalsDto(
            rows.Sum(r => r.Total),
            rows.Sum(r => r.Warehouses),
            rows.Sum(r => r.Branches),
            rows.Sum(r => r.Available),
            rows.Sum(r => r.Rented),
            rows.Sum(r => r.Maintenance),
            rows.Sum(r => r.Scrap),
            rows.Sum(r => r.StillOut),
            rows.Sum(r => r.UnexplainedOnRent),
            rows.Sum(r => r.LeftToIssue),
            otherBranches.Count > 0 ? rows.Sum(r => r.OtherAvailable ?? 0m) : null,
            rows.Sum(r => r.InTransit));

        return new ItemStatusReportDto(DateTime.UtcNow, scope, rows, totals);
    }

    /// <summary>
    /// Loads comprehensive details for a single product: location breakdown, open rental batches,
    /// left-to-issue commitments, and chronological stock movements. Customers and contracts of
    /// branches outside the viewer's full scope are never exposed.
    /// </summary>
    public async Task<ItemStatusDetailDto?> DetailAsync(
        Guid productId, ItemStatusViewer viewer, ItemStatusFilter f, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct);
        if (product is null) return null;

        var branches = await db.Branches.AsNoTracking().Where(b => !b.IsDeleted).ToListAsync(ct);
        var branchMap = branches.ToDictionary(b => b.Id);

        var (fullBranches, otherBranches, scope) = ResolveLocations(branches, viewer, f);
        var fullBranchIds = fullBranches.Select(b => b.Id).ToHashSet();
        var otherBranchIds = otherBranches.Select(b => b.Id).ToHashSet();
        var targetBranchIds = fullBranchIds.Concat(otherBranchIds).ToHashSet();

        var stocks = await db.ProductStocks.AsNoTracking()
            .Where(s => targetBranchIds.Contains(s.BranchId) && s.ProductId == productId)
            .ToListAsync(ct);
        var stocksByBranch = stocks.ToDictionary(s => s.BranchId);

        var allOutstanding = await rentals.OutstandingAsync(productId, ct);
        var outstandingInFull = allOutstanding.Where(b => fullBranchIds.Contains(b.IssueBranchId)).ToList();

        // Left to issue per open contract in full
        var openContractsQuery = db.RentalContracts.AsNoTracking()
            .Where(c => c.Status == RentalContractStatus.Open && fullBranchIds.Contains(c.BranchId));
        if (f.ContractId.HasValue) openContractsQuery = openContractsQuery.Where(c => c.Id == f.ContractId.Value);
        if (f.CustomerId.HasValue) openContractsQuery = openContractsQuery.Where(c => c.CustomerId == f.CustomerId.Value);
        var openContracts = await openContractsQuery.ToListAsync(ct);
        var openContractIds = openContracts.Select(c => c.Id).ToList();

        var leftToIssueList = new List<ItemLeftToIssueDto>();
        if (openContractIds.Count > 0)
        {
            var openLines = await db.RentalContractLines.AsNoTracking()
                .Where(l => openContractIds.Contains(l.ContractId) && l.ProductId == productId)
                .ToListAsync(ct);
            var openLineIds = openLines.Select(l => l.Id).ToList();
            var issueLines = await db.IssueNoteLines.AsNoTracking()
                .Where(l => openLineIds.Contains(l.ContractLineId))
                .Select(l => new { l.ContractLineId, l.Qty })
                .ToListAsync(ct);
            var issuedByLine = issueLines.GroupBy(x => x.ContractLineId).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));

            var customerIds = openContracts.Select(c => c.CustomerId).Distinct().ToList();
            var customerNames = await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

            foreach (var line in openLines)
            {
                var remaining = Math.Max(0m, line.Qty - issuedByLine.GetValueOrDefault(line.Id, 0m));
                if (remaining > 0)
                {
                    var contract = openContracts.First(c => c.Id == line.ContractId);
                    leftToIssueList.Add(new ItemLeftToIssueDto(
                        contract.Id, contract.Number, contract.CustomerId, customerNames.GetValueOrDefault(contract.CustomerId, ""), remaining));
                }
            }
        }
        var totalLeftToIssue = leftToIssueList.Sum(x => x.Qty);

        var inFullStocks = stocks.Where(s => fullBranchIds.Contains(s.BranchId)).ToList();
        var inOtherStocks = stocks.Where(s => otherBranchIds.Contains(s.BranchId)).ToList();
        var stillOutTotal = outstandingInFull.Sum(b => b.Qty);
        var transitLines = (await OpenTransferLinesAsync([productId], ct))
            .Where(l => fullBranchIds.Contains(l.Transfer!.FromBranchId) || fullBranchIds.Contains(l.Transfer!.ToBranchId))
            .ToList();
        decimal TransitFrom(Guid branchId) => transitLines.Where(l => l.Transfer!.FromBranchId == branchId).Sum(l => l.InTransitQty);
        var itemRow = ComputeRow(product, inFullStocks, inOtherStocks, branchMap, stillOutTotal, totalLeftToIssue, otherBranches.Count > 0,
            transitLines.Where(l => fullBranchIds.Contains(l.Transfer!.FromBranchId)).Sum(l => l.InTransitQty));
        var inTransitList = transitLines
            .OrderByDescending(l => l.Transfer!.DispatchedAtUtc)
            .Select(l =>
            {
                var t = l.Transfer!;
                var fromB = branchMap.GetValueOrDefault(t.FromBranchId);
                var toB = branchMap.GetValueOrDefault(t.ToBranchId);
                return new ItemInTransitDto(t.Id, t.Number, t.Status.ToString(),
                    t.FromBranchId, fromB?.NameAr ?? "", fromB?.NameFr ?? "", fromB?.NameEn ?? "",
                    t.ToBranchId, toB?.NameAr ?? "", toB?.NameFr ?? "", toB?.NameEn ?? "",
                    t.DispatchedAtUtc, l.InTransitQty, fullBranchIds.Contains(t.FromBranchId));
            })
            .ToList();

        // Locations
        int LocationRank(Branch b) => (b.IsWarehouse, b.IsMain) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => 2,
            _ => 3
        };

        var locationDtos = new List<ItemLocationDto>();
        foreach (var branch in fullBranches.Where(b => stocksByBranch.ContainsKey(b.Id)))
        {
            var s = stocksByBranch[branch.Id];
            var branchStillOut = outstandingInFull.Where(b => b.IssueBranchId == branch.Id).Sum(b => b.Qty);
            var branchUnexplained = s.OnRentQty - branchStillOut;
            locationDtos.Add(new ItemLocationDto(
                branch.Id, branch.Code, branch.NameAr, branch.NameFr, branch.NameEn,
                branch.IsWarehouse, branch.IsMain, branch.IsActive, AvailableOnly: false,
                Available: s.StockQty, OnRent: s.OnRentQty, Maintenance: s.MaintenanceQty, Scrap: s.ScrapQty,
                Total: s.OwnedQty + TransitFrom(branch.Id),
                StillOut: branchStillOut, UnexplainedOnRent: branchUnexplained, InTransit: TransitFrom(branch.Id)));
        }

        foreach (var branch in otherBranches.Where(b => stocksByBranch.ContainsKey(b.Id)))
        {
            var s = stocksByBranch[branch.Id];
            locationDtos.Add(new ItemLocationDto(
                branch.Id, branch.Code, branch.NameAr, branch.NameFr, branch.NameEn,
                branch.IsWarehouse, branch.IsMain, branch.IsActive, AvailableOnly: true,
                Available: s.StockQty, OnRent: null, Maintenance: null, Scrap: null, Total: null,
                StillOut: null, UnexplainedOnRent: null, InTransit: null));
        }

        locationDtos = locationDtos
            .OrderBy(l => LocationRank(branchMap[l.BranchId]))
            .ThenBy(l => branchMap[l.BranchId].NameFr)
            .ToList();

        // Rented
        var rentedQuery = outstandingInFull.AsEnumerable();
        if (f.ContractId.HasValue) rentedQuery = rentedQuery.Where(b => b.ContractId == f.ContractId.Value);

        var rentedBatches = rentedQuery.ToList();
        var rentedContractIds = rentedBatches.Select(b => b.ContractId).Distinct().ToList();
        var rentedContracts = await db.RentalContracts.AsNoTracking()
            .Where(c => rentedContractIds.Contains(c.Id))
            .ToListAsync(ct);

        if (f.CustomerId.HasValue)
        {
            var matchingContractIds = rentedContracts.Where(c => c.CustomerId == f.CustomerId.Value).Select(c => c.Id).ToHashSet();
            rentedBatches = rentedBatches.Where(b => matchingContractIds.Contains(b.ContractId)).ToList();
            rentedContracts = rentedContracts.Where(c => matchingContractIds.Contains(c.Id)).ToList();
        }

        var rentedContractMap = rentedContracts.ToDictionary(c => c.Id);
        var rentedCustomerIds = rentedContracts.Select(c => c.CustomerId).Distinct().ToList();
        var rentedCustomerMap = await db.Customers.AsNoTracking()
            .Where(c => rentedCustomerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var rentedIssueNoteIds = rentedBatches.Select(b => b.IssueNoteId).Distinct().ToList();
        var rentedIssueNoteMap = await db.IssueNotes.AsNoTracking()
            .Where(n => rentedIssueNoteIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);

        var today = rentals.Today();

        var rentedList = rentedBatches.Select(b =>
        {
            var contract = rentedContractMap[b.ContractId];
            var customer = rentedCustomerMap.GetValueOrDefault(contract.CustomerId);
            var issueNote = rentedIssueNoteMap.GetValueOrDefault(b.IssueNoteId);
            var issueBranch = branchMap[b.IssueBranchId];
            var overdue = b.EndsOn < today;

            return new ItemRentedDto(
                contract.Id,
                contract.Number,
                contract.Status.ToString(),
                contract.CustomerId,
                customer?.Code ?? "",
                customer?.FullName ?? "",
                b.IssueBranchId,
                issueBranch.NameAr,
                issueBranch.NameFr,
                issueBranch.NameEn,
                contract.SiteName,
                b.IssueNoteId,
                issueNote?.Number ?? "",
                b.IssuedOn,
                b.EndsOn,
                overdue,
                b.Qty);
        })
        .OrderBy(r => r.ExpectedReturnOn)
        .ThenBy(r => r.ContractNumber)
        .ToList();

        // Movements (full scope only)
        var allMovements = await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId && fullBranchIds.Contains(m.BranchId))
            .OrderBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var zone = Zone();
        DateTime? fromUtc = from.HasValue ? TimeZoneInfo.ConvertTimeToUtc(from.Value.ToDateTime(TimeOnly.MinValue), zone) : null;
        DateTime? toUtc = to.HasValue ? TimeZoneInfo.ConvertTimeToUtc(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), zone) : null;

        var beforeWindow = allMovements.Where(m => fromUtc.HasValue && m.CreatedAtUtc < fromUtc.Value).ToList();
        var inWindow = allMovements.Where(m => (!fromUtc.HasValue || m.CreatedAtUtc >= fromUtc.Value) && (!toUtc.HasValue || m.CreatedAtUtc < toUtc.Value)).ToList();

        var initialOpening = beforeWindow.Sum(m => m.QtyDelta);

        var docIds = inWindow.Where(m => m.DocumentId.HasValue).Select(m => m.DocumentId!.Value).Distinct().ToList();
        var saleIds = inWindow.Where(m => m.SaleId.HasValue).Select(m => m.SaleId!.Value).Distinct().ToList();
        var supplierIds = inWindow.Where(m => m.SupplierId.HasValue).Select(m => m.SupplierId!.Value).Distinct().ToList();

        var issueNotes = await db.IssueNotes.AsNoTracking().Include(n => n.Contract).ThenInclude(c => c!.Customer)
            .Where(n => docIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var returnNotes = await db.ReturnNotes.AsNoTracking().Include(n => n.Contract).ThenInclude(c => c!.Customer)
            .Where(n => docIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var sales = await db.Sales.AsNoTracking().Include(s => s.Customer)
            .Where(s => saleIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        var issueLinesForProduct = await db.IssueNoteLines.AsNoTracking()
            .Where(l => docIds.Contains(l.IssueNoteId) && l.ContractLine!.ProductId == productId)
            .Select(l => new { l.IssueNoteId, l.Qty })
            .ToListAsync(ct);
        var returnLinesForProduct = await db.ReturnNoteLines.AsNoTracking()
            .Where(l => docIds.Contains(l.ReturnNoteId) && l.ContractLine!.ProductId == productId)
            .Select(l => new { l.ReturnNoteId, Settled = l.QtyOk + l.QtyPartialDamage + l.QtyTotalDamage + l.QtyMissing })
            .ToListAsync(ct);

        var moveCountByDocId = allMovements.Where(m => m.DocumentId.HasValue)
            .GroupBy(m => m.DocumentId!.Value).ToDictionary(g => g.Key, g => g.Count());

        var orderedRows = MergeTransferMovements(inWindow);

        var runningBalance = initialOpening;
        var movementDtos = new List<ItemMovementDto>();

        foreach (var item in orderedRows)
        {
            if (item.IsMerged)
            {
                var neg = item.Row;
                var pos = item.Partner!;
                var qty = Math.Abs(neg.QtyDelta);

                var type = neg.Reason.Equals("rental-return-transfer", StringComparison.OrdinalIgnoreCase)
                    ? "rental-return-transfer"
                    : "transfer";
                var detail = ExtractDetail(neg.Reason);

                var docNum = neg.DocumentNumber ?? pos.DocumentNumber;
                var userName = neg.UserName ?? pos.UserName;

                // Balance unchanged on internal transfer pair
                movementDtos.Add(new ItemMovementDto(
                    item.AtUtc,
                    type,
                    FromState: null,
                    ToState: null,
                    detail,
                    docNum,
                    neg.DocumentId,
                    SaleId: null,
                    ContractId: null,
                    FromBranchId: neg.BranchId,
                    ToBranchId: pos.BranchId,
                    CustomerName: null,
                    SupplierName: null,
                    Moved: qty,
                    In: qty,
                    Out: qty,
                    Balance: runningBalance,
                    userName));
            }
            else
            {
                var m = item.Row;
                var delta = m.QtyDelta;
                var (type, fromState, toState, detail) = ParseReason(m.Reason);

                Guid? fromBranch = null;
                Guid? toBranch = null;

                if (type == "transfer" || type == "rental-return-transfer")
                {
                    if (delta < 0) { fromBranch = m.BranchId; toBranch = m.RelatedBranchId; }
                    else { fromBranch = m.RelatedBranchId; toBranch = m.BranchId; }
                }
                else if (type == StockTransferReasons.Dispatch || type == StockTransferReasons.Loss)
                {
                    fromBranch = m.BranchId; toBranch = m.RelatedBranchId;
                }
                else if (type == StockTransferReasons.Receive || type == StockTransferReasons.Cancel)
                {
                    // A receipt arrives from the source; a cancel comes back from the destination it never reached.
                    fromBranch = m.RelatedBranchId; toBranch = m.BranchId;
                }
                else if (type == "sale" || type == "rental-issue")
                {
                    fromBranch = m.BranchId;
                }
                else if (type == "refund" || type == "receiving" || type == "opening" || type == "rental-return")
                {
                    toBranch = m.BranchId;
                }
                else if (type == "state")
                {
                    fromBranch = m.BranchId;
                    toBranch = m.BranchId;
                }
                else // adjustment
                {
                    if (delta < 0) fromBranch = m.BranchId;
                    else if (delta > 0) toBranch = m.BranchId;
                }

                string? docNumber = m.DocumentNumber;
                string? userName = m.UserName;
                string? customerName = null;
                string? supplierName = null;
                Guid? contractId = null;

                if (m.DocumentId.HasValue)
                {
                    if (issueNotes.TryGetValue(m.DocumentId.Value, out var inote))
                    {
                        docNumber ??= inote.Number;
                        userName ??= inote.UserName;
                        contractId = inote.ContractId;
                        customerName = inote.Contract?.Customer?.FullName;
                    }
                    else if (returnNotes.TryGetValue(m.DocumentId.Value, out var rnote))
                    {
                        docNumber ??= rnote.Number;
                        userName ??= rnote.UserName;
                        contractId = rnote.ContractId;
                        customerName = rnote.Contract?.Customer?.FullName;
                    }
                }

                if (m.SaleId.HasValue && sales.TryGetValue(m.SaleId.Value, out var sale))
                {
                    docNumber ??= sale.Reference;
                    userName ??= sale.CashierName;
                    customerName ??= sale.Customer?.FullName;
                }

                if (m.SupplierId.HasValue && suppliers.TryGetValue(m.SupplierId.Value, out var supp))
                {
                    supplierName = supp.Name;
                }

                decimal? moved = m.MovedQty;
                if (moved is null)
                {
                    if (delta != 0)
                    {
                        moved = Math.Abs(delta);
                    }
                    else if (type == "rental-issue" && m.DocumentId.HasValue && moveCountByDocId.GetValueOrDefault(m.DocumentId.Value) == 1)
                    {
                        moved = issueLinesForProduct.Where(l => l.IssueNoteId == m.DocumentId.Value).Sum(l => l.Qty);
                    }
                    else if (type == "rental-return" && m.DocumentId.HasValue && moveCountByDocId.GetValueOrDefault(m.DocumentId.Value) == 1)
                    {
                        moved = returnLinesForProduct.Where(l => l.ReturnNoteId == m.DocumentId.Value).Sum(l => l.Settled);
                    }
                }

                var inQty = Math.Max(delta, 0m);
                var outQty = Math.Max(-delta, 0m);
                runningBalance += delta;

                movementDtos.Add(new ItemMovementDto(
                    m.CreatedAtUtc,
                    type,
                    fromState,
                    toState,
                    detail,
                    docNumber,
                    m.DocumentId,
                    m.SaleId,
                    contractId,
                    fromBranch,
                    toBranch,
                    customerName,
                    supplierName,
                    moved,
                    inQty,
                    outQty,
                    runningBalance,
                    userName));
            }
        }

        bool truncated = false;
        decimal opening = initialOpening;
        IReadOnlyList<ItemMovementDto> finalRows = movementDtos;

        if (movementDtos.Count > 1000)
        {
            truncated = true;
            var skipped = movementDtos.Take(movementDtos.Count - 1000).ToList();
            finalRows = movementDtos.Skip(movementDtos.Count - 1000).ToList();
            opening = skipped.Last().Balance;
        }

        var closing = finalRows.Count > 0 ? finalRows.Last().Balance : opening;

        decimal? unrecordedDifference = null;
        if (to is null || to.Value >= today)
        {
            // The history already shows a dispatch as an "out" at its source, so goods still in transit
            // are compared away: they are not a difference nobody recorded.
            var diff = itemRow.Total - itemRow.InTransit - closing;
            if (diff != 0) unrecordedDifference = diff;
        }

        var movementsResult = new ItemMovementsDto(from, to, opening, closing, unrecordedDifference, truncated, finalRows);

        return new ItemStatusDetailDto(
            DateTime.UtcNow,
            scope,
            itemRow,
            locationDtos,
            rentedList,
            leftToIssueList,
            movementsResult,
            inTransitList);
    }

    /// <summary>
    /// Merges complementary transfer movement pairs (same DocumentId, opposite deltas) into a single row
    /// with In = Out = Qty, leaving the net balance unchanged. When only one side of the transfer is within
    /// the viewer's scope, it remains an unmerged single-ended movement.
    /// </summary>
    private static List<(DateTime AtUtc, StockMovement Row, StockMovement? Partner, bool IsMerged)> MergeTransferMovements(List<StockMovement> inWindow)
    {
        var consumedRowIds = new HashSet<Guid>();
        var orderedRows = new List<(DateTime AtUtc, StockMovement Row, StockMovement? Partner, bool IsMerged)>();

        for (var i = 0; i < inWindow.Count; i++)
        {
            var m = inWindow[i];
            if (consumedRowIds.Contains(m.Id)) continue;

            bool isTransferReason = m.Reason.StartsWith("transfer", StringComparison.OrdinalIgnoreCase) ||
                                    m.Reason.Equals("rental-return-transfer", StringComparison.OrdinalIgnoreCase);

            if (m.DocumentId.HasValue && isTransferReason)
            {
                var partnerIndex = inWindow.FindIndex(i + 1, other =>
                    !consumedRowIds.Contains(other.Id) &&
                    other.DocumentId == m.DocumentId &&
                    other.QtyDelta == -m.QtyDelta &&
                    (other.Reason.StartsWith("transfer", StringComparison.OrdinalIgnoreCase) ||
                     other.Reason.Equals("rental-return-transfer", StringComparison.OrdinalIgnoreCase)));

                if (partnerIndex >= 0)
                {
                    var partner = inWindow[partnerIndex];
                    consumedRowIds.Add(m.Id);
                    consumedRowIds.Add(partner.Id);
                    var neg = m.QtyDelta < 0 ? m : partner;
                    var pos = m.QtyDelta > 0 ? m : partner;
                    orderedRows.Add((m.CreatedAtUtc, neg, pos, true));
                    continue;
                }
            }

            consumedRowIds.Add(m.Id);
            orderedRows.Add((m.CreatedAtUtc, m, null, false));
        }

        return orderedRows;
    }

    private static (List<Branch> Full, List<Branch> Others, ItemStatusScopeDto Scope) ResolveLocations(
        List<Branch> branches, ItemStatusViewer viewer, ItemStatusFilter f)
    {
        List<Branch> fullBranches;
        List<Branch> otherBranches;
        ItemStatusScopeDto scope;

        if (viewer.OwnBranch.HasValue)
        {
            fullBranches = branches.Where(b => b.Id == viewer.OwnBranch.Value).ToList();
            otherBranches = viewer.OthersAvailable ? branches.Where(b => b.Id != viewer.OwnBranch.Value).ToList() : [];
            scope = new ItemStatusScopeDto("own", viewer.OwnBranch.Value, otherBranches.Count > 0);
        }
        else if (f.LocationId.HasValue)
        {
            fullBranches = branches.Where(b => b.Id == f.LocationId.Value).ToList();
            otherBranches = [];
            scope = new ItemStatusScopeDto("location", f.LocationId.Value, false);
        }
        else
        {
            fullBranches = branches.ToList();
            otherBranches = [];
            scope = new ItemStatusScopeDto("company", null, false);
        }

        return (fullBranches, otherBranches, scope);
    }

    /// <summary>
    /// Computes one article's line in the report. By construction, Total = Warehouses + Branches + Rented + Maintenance + Scrap
    /// + InTransit across the locations the caller sees in full. In-transit goods count at the location that dispatched
    /// them until they are received (owner, 2026-10-12; StockTransfer): they left its available bucket at dispatch and enter
    /// the destination's only on receipt, so the company scope counts each unit exactly once. Other locations are excluded from Total because they are not under
    /// the caller's governance, though their available quantity is optionally reported as OtherAvailable for visibility.
    /// </summary>
    private static ItemStatusRowDto ComputeRow(
        Product p,
        IReadOnlyList<ProductStock> productStocksInFull,
        IReadOnlyList<ProductStock> productStocksInOthers,
        IReadOnlyDictionary<Guid, Branch> branchMap,
        decimal stillOut,
        decimal leftToIssue,
        bool showOtherAvailable,
        decimal inTransit)
    {
        var warehouses = productStocksInFull.Where(s => branchMap[s.BranchId].IsWarehouse).Sum(s => s.StockQty);
        var branchesQty = productStocksInFull.Where(s => !branchMap[s.BranchId].IsWarehouse).Sum(s => s.StockQty);
        var available = warehouses + branchesQty;
        var rented = productStocksInFull.Sum(s => s.OnRentQty);
        var maintenance = productStocksInFull.Sum(s => s.MaintenanceQty);
        var scrap = productStocksInFull.Sum(s => s.ScrapQty);
        var total = warehouses + branchesQty + rented + maintenance + scrap + inTransit;
        var unexplainedOnRent = rented - stillOut;
        decimal? otherAvailable = showOtherAvailable ? productStocksInOthers.Sum(s => s.StockQty) : null;

        return new ItemStatusRowDto(
            p.Id, p.Sku, p.Barcode, p.NameAr, p.NameFr, p.NameEn, p.Unit,
            p.IsRentable, p.IsActive,
            total, warehouses, branchesQty, available,
            rented, maintenance, scrap,
            stillOut, unexplainedOnRent, leftToIssue,
            otherAvailable, inTransit);
    }

    private static bool MatchesStatusFilter(ItemStatusRowDto row, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        return status.ToLowerInvariant() switch
        {
            "available" => row.Available > 0,
            "rented" => row.Rented > 0,
            "maintenance" => row.Maintenance > 0,
            "scrap" => row.Scrap > 0,
            "unexplained" => row.UnexplainedOnRent != 0,
            "transit" => row.InTransit > 0,
            _ => true,
        };
    }

    private static (string Type, string? FromState, string? ToState, string? Detail) ParseReason(string reason)
    {
        if (string.Equals(reason, "sale", StringComparison.OrdinalIgnoreCase))
            return ("sale", null, null, null);
        if (string.Equals(reason, "refund", StringComparison.OrdinalIgnoreCase))
            return ("refund", null, null, null);
        if (reason.StartsWith("receiving", StringComparison.OrdinalIgnoreCase))
            return ("receiving", null, null, ExtractDetail(reason));
        if (reason.StartsWith("opening", StringComparison.OrdinalIgnoreCase))
            return ("opening", null, null, ExtractDetail(reason));
        // The transfer workflow's four steps (StockTransferReasons), then the instant transfer's rows of before.
        foreach (var step in TransferSteps)
            if (reason.Equals(step, StringComparison.OrdinalIgnoreCase) || reason.StartsWith(step + ":", StringComparison.OrdinalIgnoreCase))
                return (step, null, null, ExtractDetail(reason));
        if (reason.StartsWith("transfer", StringComparison.OrdinalIgnoreCase))
            return ("transfer", null, null, ExtractDetail(reason));
        if (string.Equals(reason, "rental-issue", StringComparison.OrdinalIgnoreCase))
            return ("rental-issue", null, null, null);
        if (string.Equals(reason, "rental-return", StringComparison.OrdinalIgnoreCase))
            return ("rental-return", null, null, null);
        if (string.Equals(reason, "rental-return-transfer", StringComparison.OrdinalIgnoreCase))
            return ("rental-return-transfer", null, null, null);

        var match = StateTransitionRegex.Match(reason);
        if (match.Success)
        {
            var fromState = match.Groups[1].Value.ToLowerInvariant();
            var toState = match.Groups[2].Value.ToLowerInvariant();
            var detail = match.Groups[3].Success && !string.IsNullOrWhiteSpace(match.Groups[3].Value) ? match.Groups[3].Value.Trim() : null;
            return ("state", fromState, toState, detail);
        }

        return ("adjustment", null, null, reason);
    }

    private static readonly string[] TransferSteps =
        [StockTransferReasons.Dispatch, StockTransferReasons.Receive, StockTransferReasons.Cancel, StockTransferReasons.Loss];

    /// <summary>
    /// The open transfer lines of these products with something left in transit. The remainder is a
    /// decimal, so it is filtered after the read (SQLite stores decimals as text).
    /// </summary>
    private async Task<List<StockTransferLine>> OpenTransferLinesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        var ids = productIds.ToList();
        var lines = await db.StockTransferLines.AsNoTracking().Include(l => l.Transfer)
            .Where(l => ids.Contains(l.ProductId)
                && (l.Transfer!.Status == StockTransferStatus.Dispatched || l.Transfer!.Status == StockTransferStatus.PartlyReceived))
            .ToListAsync(ct);
        return lines.Where(l => l.InTransitQty > 0).ToList();
    }

    private static string? ExtractDetail(string reason)
    {
        var idx = reason.IndexOf(": ", StringComparison.Ordinal);
        if (idx >= 0 && idx + 2 < reason.Length)
        {
            var detail = reason[(idx + 2)..].Trim();
            return string.IsNullOrEmpty(detail) ? null : detail;
        }
        return null;
    }

    // ---------- Excel workbooks ----------

    public byte[] SummaryWorkbook(ItemStatusReportDto r, ItemStatusLabels l)
    {
        var columns = new List<XlsxColumn>
        {
            new(l.Reference, XlsxKind.Text),
            new(l.Item, XlsxKind.Text),
            new(l.Unit, XlsxKind.Text),
            new(l.ItemType, XlsxKind.Text),
            new(l.Total, XlsxKind.Number, Decimals: 3),
            new(l.Warehouses, XlsxKind.Number, Decimals: 3),
            new(l.Branches, XlsxKind.Number, Decimals: 3),
            new(l.Rented, XlsxKind.Number, Decimals: 3),
            new(l.Maintenance, XlsxKind.Number, Decimals: 3),
            new(l.Scrap, XlsxKind.Number, Decimals: 3),
            new(l.Available, XlsxKind.Number, Decimals: 3),
            new(l.InTransit, XlsxKind.Number, Decimals: 3),
            new(l.ContractsStillOut, XlsxKind.Number, Decimals: 3),
            new(l.UnexplainedOnRent, XlsxKind.Number, Decimals: 3),
            new(l.LeftToIssue, XlsxKind.Number, Decimals: 3),
        };

        if (r.Scope.OthersAvailableShown)
        {
            columns.Add(new(l.OtherAvailable, XlsxKind.Number, Decimals: 3));
        }

        var rows = new List<IReadOnlyList<object?>>();
        foreach (var row in r.Rows)
        {
            var list = new List<object?>
            {
                row.Sku,
                l.Name(row.NameAr, row.NameFr, row.NameEn),
                row.Unit,
                row.IsRentable ? l.Rental : l.Sale,
                row.Total,
                row.Warehouses,
                row.Branches,
                row.Rented,
                row.Maintenance,
                row.Scrap,
                row.Available,
                row.InTransit,
                row.StillOut,
                row.UnexplainedOnRent,
                row.LeftToIssue,
            };
            if (r.Scope.OthersAvailableShown)
            {
                list.Add(row.OtherAvailable);
            }
            rows.Add(list);
        }

        var totals = new List<object?>
        {
            l.Totals,
            null,
            null,
            null,
            r.Totals.Total,
            r.Totals.Warehouses,
            r.Totals.Branches,
            r.Totals.Rented,
            r.Totals.Maintenance,
            r.Totals.Scrap,
            r.Totals.Available,
            r.Totals.InTransit,
            r.Totals.StillOut,
            r.Totals.UnexplainedOnRent,
            r.Totals.LeftToIssue,
        };
        if (r.Scope.OthersAvailableShown)
        {
            totals.Add(r.Totals.OtherAvailable);
        }

        var sheet = new XlsxSheet
        {
            Name = l.Title,
            RightToLeft = l.RightToLeft,
            Title =
            [
                l.Title,
                string.Create(CultureInfo.InvariantCulture, $"{l.AsOf} {TimeZoneInfo.ConvertTimeFromUtc(r.AsOfUtc, Zone()):yyyy-MM-dd HH:mm}"),
                l.ScopeWord(r.Scope.Kind),
                l.InTransitNote
            ],
            Columns = columns,
            Rows = rows,
            Totals = totals,
        };

        return XlsxWriter.Write(sheet);
    }

    public byte[] DetailWorkbook(ItemStatusDetailDto d, ItemStatusLabels l, IReadOnlyDictionary<Guid, Branch>? branches = null)
    {
        var branchMap = branches ?? db.Branches.AsNoTracking().ToDictionary(b => b.Id);
        var zone = Zone();

        var titleLines = new List<string>
        {
            l.Title,
            $"{l.Name(d.Item.NameAr, d.Item.NameFr, d.Item.NameEn)} ({d.Item.Sku})",
            string.Create(CultureInfo.InvariantCulture, $"{l.AsOf} {TimeZoneInfo.ConvertTimeFromUtc(d.AsOfUtc, zone):yyyy-MM-dd HH:mm}"),
            l.ScopeWord(d.Scope.Kind),
            l.InTransitNote
        };

        // 1. Locations sheet
        var locationColumns = new List<XlsxColumn>
        {
            new(l.Location, XlsxKind.Text),
            new(l.Reference, XlsxKind.Text),
            new(l.Kind, XlsxKind.Text),
            new(l.Inactive, XlsxKind.Text),
            new(l.Available, XlsxKind.Number, Decimals: 3),
            new(l.Rented, XlsxKind.Number, Decimals: 3),
            new(l.Maintenance, XlsxKind.Number, Decimals: 3),
            new(l.Scrap, XlsxKind.Number, Decimals: 3),
            new(l.InTransit, XlsxKind.Number, Decimals: 3),
            new(l.Total, XlsxKind.Number, Decimals: 3),
            new(l.ContractsStillOut, XlsxKind.Number, Decimals: 3),
            new(l.UnexplainedOnRent, XlsxKind.Number, Decimals: 3),
        };

        var locationRows = new List<IReadOnlyList<object?>>();
        foreach (var loc in d.Locations)
        {
            locationRows.Add([
                l.Name(loc.NameAr, loc.NameFr, loc.NameEn),
                loc.Code,
                loc.IsWarehouse ? l.Warehouse : l.Branch,
                loc.IsActive ? "" : l.Inactive,
                loc.Available,
                loc.OnRent,
                loc.Maintenance,
                loc.Scrap,
                loc.InTransit,
                loc.Total,
                loc.StillOut,
                loc.UnexplainedOnRent
            ]);
        }

        var locationsSheet = new XlsxSheet
        {
            Name = l.Location,
            RightToLeft = l.RightToLeft,
            Title = titleLines,
            Columns = locationColumns,
            Rows = locationRows,
        };

        // 2. Rented sheet
        var rentedColumns = new List<XlsxColumn>
        {
            new(l.Contract, XlsxKind.Text),
            new(l.Customer, XlsxKind.Text),
            new(l.IssueBranch, XlsxKind.Text),
            new(l.Site, XlsxKind.Text),
            new(l.IssueNote, XlsxKind.Text),
            new(l.IssueDate, XlsxKind.Date),
            new(l.ExpectedReturn, XlsxKind.Date),
            new(l.Overdue, XlsxKind.Text),
            new(l.Qty, XlsxKind.Number, Decimals: 3),
        };

        var rentedRows = new List<IReadOnlyList<object?>>();
        foreach (var r in d.Rented)
        {
            rentedRows.Add([
                r.ContractNumber,
                $"{r.CustomerCode} - {r.CustomerName}",
                l.Name(r.IssueBranchNameAr, r.IssueBranchNameFr, r.IssueBranchNameEn),
                r.SiteName ?? "",
                r.IssueNoteNumber,
                r.IssuedOn.ToDateTime(TimeOnly.MinValue),
                r.ExpectedReturnOn.ToDateTime(TimeOnly.MinValue),
                r.Overdue ? l.Overdue : "",
                r.Qty
            ]);
        }

        var rentedSheet = new XlsxSheet
        {
            Name = l.Rented,
            RightToLeft = l.RightToLeft,
            Title = titleLines,
            Columns = rentedColumns,
            Rows = rentedRows,
        };

        // 3. Movements sheet
        var movementColumns = new List<XlsxColumn>
        {
            new(l.Date, XlsxKind.Date),
            new(l.Document, XlsxKind.Text),
            new(l.Type, XlsxKind.Text),
            new(l.Detail, XlsxKind.Text),
            new(l.From, XlsxKind.Text),
            new(l.To, XlsxKind.Text),
            new(l.Supplier, XlsxKind.Text),
            new(l.Customer, XlsxKind.Text),
            new(l.Moved, XlsxKind.Number, Decimals: 3),
            new(l.In, XlsxKind.Number, Decimals: 3),
            new(l.Out, XlsxKind.Number, Decimals: 3),
            new(l.Balance, XlsxKind.Number, Decimals: 3),
            new(l.User, XlsxKind.Text),
        };

        var movementRows = new List<IReadOnlyList<object?>>();
        // Opening balance as first row
        movementRows.Add([
            null,
            null,
            l.OpeningBalance,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            d.Movements.Opening,
            null
        ]);

        string BranchName(Guid? id) =>
            id.HasValue && branchMap.TryGetValue(id.Value, out var b) ? l.Name(b.NameAr, b.NameFr, b.NameEn) : "";

        foreach (var m in d.Movements.Rows)
        {
            var movementDate = TimeZoneInfo.ConvertTimeFromUtc(m.AtUtc, zone);

            string typeCell = m.Type == "state"
                ? $"{l.MovementType("state")}: {l.State(m.FromState ?? "")} → {l.State(m.ToState ?? "")}"
                : l.MovementType(m.Type);

            string fromText = m.Type switch
            {
                "rental-return" or "refund" => m.CustomerName ?? "",
                "receiving" => m.SupplierName ?? "",
                _ => BranchName(m.FromBranchId)
            };

            string toText = m.Type switch
            {
                "rental-issue" or "sale" => m.CustomerName ?? "",
                _ => BranchName(m.ToBranchId)
            };

            movementRows.Add([
                movementDate,
                m.DocumentNumber ?? "",
                typeCell,
                m.Detail ?? "",
                fromText,
                toText,
                m.SupplierName ?? "",
                m.CustomerName ?? "",
                m.Moved,
                m.In > 0 ? m.In : null,
                m.Out > 0 ? m.Out : null,
                m.Balance,
                m.UserName ?? ""
            ]);
        }

        // Closing balance as last row
        movementRows.Add([
            null,
            null,
            l.ClosingBalance,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            d.Movements.Closing,
            null
        ]);

        var movementsSheet = new XlsxSheet
        {
            Name = l.Movements,
            RightToLeft = l.RightToLeft,
            Title = titleLines,
            Columns = movementColumns,
            Rows = movementRows,
        };

        // 4. In transit: what is on its way to or from the locations seen, with where it counts.
        if (d.InTransit.Count == 0) return XlsxWriter.Write(locationsSheet, rentedSheet, movementsSheet);
        var transitSheet = new XlsxSheet
        {
            Name = l.InTransit,
            RightToLeft = l.RightToLeft,
            Title = titleLines,
            Columns =
            [
                new(l.Document, XlsxKind.Text),
                new(l.Status, XlsxKind.Text),
                new(l.From, XlsxKind.Text),
                new(l.To, XlsxKind.Text),
                new(l.DispatchedOn, XlsxKind.Date),
                new(l.Qty, XlsxKind.Number, Decimals: 3),
                new(l.CountedHere, XlsxKind.Text),
            ],
            Rows = d.InTransit.Select(t => (IReadOnlyList<object?>)
            [
                t.Number,
                l.TransferStatus(t.Status),
                l.Name(t.FromNameAr, t.FromNameFr, t.FromNameEn),
                l.Name(t.ToNameAr, t.ToNameFr, t.ToNameEn),
                TimeZoneInfo.ConvertTimeFromUtc(t.DispatchedAtUtc, zone),
                t.Qty,
                t.CountedHere ? l.Yes : l.No,
            ]).ToList(),
        };
        return XlsxWriter.Write(locationsSheet, rentedSheet, movementsSheet, transitSheet);
    }
}
