using System.Globalization;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Rentals;

namespace ParadisERP.Tests.Rentals;

public class QuantityStatementTests
{
    private static readonly RegionalSettings Money = new() { CurrencyDecimals = 2 };
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly QuantityStatementHeader Header = new(Guid.NewGuid(), "C-001", "Entreprise Test", false, null);
    private static DateOnly D(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);
    private static DateTime At(string iso) => D(iso).ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);

    private sealed class Paper(string number, int minimumDays = 0)
    {
        private readonly List<RentalLine> lines = [];
        private readonly List<QuantityLine> meta = [];
        private readonly List<IssueBatch> issues = [];
        private readonly List<ReturnEntry> returns = [];
        private readonly List<RentalExtension> extensions = [];
        private readonly List<QuantityMove> moves = [];
        public Guid Id { get; } = Guid.NewGuid();
        public bool CorrectionPending { get; init; }

        public Guid Line(Guid product, decimal rent, int duration = 60, decimal overdue = 0, Guid? parent = null)
        {
            var line = new RentalLine(Guid.NewGuid(), 1000, duration, rent, 0, overdue, parent);
            lines.Add(line);
            meta.Add(new QuantityLine(line.Id, product, "SKU-" + product.ToString()[..4], "Item " + product.ToString()[..4], null, null));
            return line.Id;
        }

        public Paper Issue(Guid line, string on, decimal qty, bool carryOver = false)
        {
            var note = Guid.NewGuid();
            issues.Add(new IssueBatch(note, line, D(on), qty));
            moves.Add(new QuantityMove(note, $"ISS-{moves.Count + 1:D3}", line, At(on), D(on), Branch,
                true, carryOver, qty, 0, 0));
            return this;
        }

        public Paper Return(Guid line, string on, decimal ok = 0, decimal partial = 0, decimal totalDamage = 0,
            decimal missing = 0, bool carryOver = false)
        {
            var note = Guid.NewGuid();
            returns.Add(new ReturnEntry(note, line, D(on), ok, partial, 0, totalDamage, missing));
            moves.Add(new QuantityMove(note, $"RET-{moves.Count + 1:D3}", line, At(on).AddHours(1), D(on), Branch,
                false, carryOver, 0, ok + partial, totalDamage + missing));
            return this;
        }

        public RentalContractInput Pricing => new(lines, issues, returns, extensions, minimumDays);

        public QuantityContract Build() => new(Id, number, Branch, null, "Chantier", null, null,
            CorrectionPending, Pricing, meta, moves);
    }

    private static QuantityStatementDto Run(string from, string to, params Paper[] papers) =>
        QuantityStatement.Build(Header, papers.Select(p => p.Build()).ToList(), D(from), D(to), Money);

    [Fact]
    public void Issued_and_fully_returned_inside_the_period()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-05", 10);
        paper.Return(line, "2026-03-15", ok: 10);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        Assert.Equal(0m, item.Opening);
        Assert.Equal(10m, item.Issued);
        Assert.Equal(10m, item.Returned);
        Assert.Equal(0m, item.Remaining);
        Assert.Equal(500m, item.Value);
        Assert.Equal(0m, item.ProvisionalValue);
        Assert.Equal(["opening", "issue", "return"], item.Movements.Select(m => m.Kind));
        Assert.Equal([0m, 10m, 0m], item.Movements.Select(m => m.Balance));
    }

    [Fact]
    public void A_partial_return_leaves_the_rest_with_the_customer_priced_provisionally()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-05", 10);
        paper.Return(line, "2026-03-15", ok: 4);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        Assert.Equal(4m, item.Returned);
        Assert.Equal(6m, item.Remaining);
        Assert.Equal(1010m, item.Value);
        Assert.Equal(810m, item.ProvisionalValue);
        Assert.Equal(["opening", "issue", "partialReturn"], item.Movements.Select(m => m.Kind));
    }

    [Fact]
    public void Goods_issued_before_From_open_the_window()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-02-20", 10);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        Assert.Equal(10m, item.Opening);
        Assert.Equal(0m, item.Issued);
        Assert.Equal(10m, item.Remaining);
        Assert.Equal(1550m, item.Value);
        Assert.Equal(1550m, item.ProvisionalValue);
        Assert.Equal("opening", item.Movements[0].Kind);
        Assert.Equal(10m, item.Movements[0].Balance);
    }

    [Fact]
    public void Goods_returned_after_To_are_still_with_the_customer_at_To()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-05", 10);
        paper.Return(line, "2026-04-10", ok: 10);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        Assert.Equal(0m, item.Returned);
        Assert.Equal(10m, item.Remaining);
        Assert.Equal(1350m, item.Value);
        Assert.Equal(0m, item.ProvisionalValue);
    }

    [Fact]
    public void Several_issues_of_one_item_close_oldest_first()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-01", 10);
        paper.Issue(line, "2026-03-10", 5);
        paper.Return(line, "2026-03-20", ok: 12);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        Assert.Equal(15m, item.Issued);
        Assert.Equal(12m, item.Returned);
        Assert.Equal(3m, item.Remaining);
        Assert.Equal(1380m, item.Value);
        Assert.Equal(330m, item.ProvisionalValue);
    }

    [Fact]
    public void One_item_under_two_contracts_is_one_row_and_two_contracts()
    {
        var prod = Guid.NewGuid();
        var paperA = new Paper("C1");
        var lineA = paperA.Line(prod, rent: 5);
        paperA.Issue(lineA, "2026-03-01", 10);

        var paperB = new Paper("C2");
        var lineB = paperB.Line(prod, rent: 5);
        paperB.Issue(lineB, "2026-03-10", 4);

        var s = Run("2026-03-01", "2026-03-31", paperA, paperB);
        var item = s.Items.Single();

        Assert.Equal(14m, item.Issued);
        Assert.Equal(2, s.Contracts.Count);
        Assert.Equal(10m, s.Contracts[0].Lines.Single().Issued);
        Assert.Equal(4m, s.Contracts[1].Lines.Single().Issued);
        Assert.Equal(3, item.Movements.Count);
    }

    [Fact]
    public void Two_daily_rates_are_both_listed()
    {
        var prod = Guid.NewGuid();
        var paperA = new Paper("C1");
        var lineA = paperA.Line(prod, rent: 5);
        paperA.Issue(lineA, "2026-03-01", 2);

        var paperB = new Paper("C2");
        var lineB = paperB.Line(prod, rent: 6);
        paperB.Issue(lineB, "2026-03-01", 2);

        var s = Run("2026-03-01", "2026-03-31", paperA, paperB);
        var item = s.Items.Single();

        Assert.Equal([new QuantityStatementRateDto("rent", 5m), new QuantityStatementRateDto("rent", 6m)], item.Rates);
        Assert.Equal(682m, item.Value);
    }

    [Fact]
    public void Overdue_days_show_their_own_rate()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5, duration: 10, overdue: 7.5m);
        paper.Issue(line, "2026-03-01", 2);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        Assert.Equal([new QuantityStatementRateDto("rent", 5m), new QuantityStatementRateDto("overdue", 7.5m)], item.Rates);
        Assert.Equal(415m, item.Value);
    }

    [Fact]
    public void Several_partial_returns_run_the_balance_down()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-01", 10);
        paper.Return(line, "2026-03-05", ok: 3);
        paper.Return(line, "2026-03-10", ok: 3);
        paper.Return(line, "2026-03-20", ok: 4);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        Assert.Equal(["opening", "issue", "partialReturn", "partialReturn", "return"], item.Movements.Select(m => m.Kind));
        Assert.Equal([0m, 10m, 7m, 4m, 0m], item.Movements.Select(m => m.Balance));
        Assert.Equal(575m, item.Value);
    }

    [Fact]
    public void Owners_example_is_13000_and_matches_the_settlement()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5, duration: 60);
        paper.Issue(line, "2026-01-01", 100);
        paper.Return(line, "2026-01-21", ok: 40);
        paper.Return(line, "2026-01-31", ok: 60);

        var s = Run("2026-01-01", "2026-01-31", paper);
        var item = s.Items.Single();

        Assert.Equal(13_000m, item.Value);
        Assert.Equal(0m, item.ProvisionalValue);
        Assert.Equal(13_000m, RentalSettlement.Settle(paper.Pricing, Money).ChargeTotal);

        var s1 = Run("2026-01-01", "2026-01-15", paper);
        var s2 = Run("2026-01-16", "2026-01-31", paper);
        Assert.Equal(13_000m, s1.Items.Single().Value + s2.Items.Single().Value);
    }

    [Fact]
    public void Missing_and_damaged_have_their_own_column()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-01", 10);
        paper.Return(line, "2026-03-10", ok: 6, partial: 1, totalDamage: 2, missing: 1);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        Assert.Equal(7m, item.Returned);
        Assert.Equal(3m, item.MissingDamaged);
        Assert.Equal(0m, item.Remaining);
    }

    [Fact]
    public void Extension_as_a_new_contract_is_a_transfer_outside_the_totals()
    {
        var prod = Guid.NewGuid();
        var paperA = new Paper("C1");
        var lineA = paperA.Line(prod, rent: 5);
        paperA.Issue(lineA, "2026-03-01", 10);
        paperA.Return(lineA, "2026-03-15", ok: 10, carryOver: true);

        var paperB = new Paper("C2");
        var lineB = paperB.Line(prod, rent: 5);
        paperB.Issue(lineB, "2026-03-15", 10, carryOver: true);

        var s = Run("2026-03-01", "2026-03-31", paperA, paperB);
        var item = s.Items.Single();

        Assert.Equal(10m, item.Issued);
        Assert.Equal(0m, item.Returned);
        Assert.Equal(0m, item.TransferNet);
        Assert.Equal(10m, item.Remaining);

        var contractA = s.Contracts.Single(c => c.Number == "C1");
        var contractB = s.Contracts.Single(c => c.Number == "C2");
        Assert.Equal(10m, contractA.Lines.Single().TransferredOut);
        Assert.Equal(0m, contractA.Lines.Single().Remaining);
        Assert.Equal(10m, contractB.Lines.Single().TransferredIn);
        Assert.Equal(10m, contractB.Lines.Single().Remaining);

        Assert.Contains("transferOut", item.Movements.Select(m => m.Kind));
        Assert.Contains("transferIn", item.Movements.Select(m => m.Kind));
    }

    [Fact]
    public void A_balance_below_zero_is_a_warning_not_a_silent_zero()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-10", 10);
        paper.Return(line, "2026-03-05", ok: 10);

        var s = Run("2026-03-01", "2026-03-31", paper);
        var item = s.Items.Single();

        var w = Assert.Single(s.Warnings, x => x.Code == "negativeBalance");
        Assert.Equal(D("2026-03-05"), w.On);
        Assert.True(item.NegativeBalance);
    }

    [Fact]
    public void A_return_bigger_than_the_issue_is_reported()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("C1");
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-01", 10);
        paper.Return(line, "2026-03-10", ok: 12);

        var s = Run("2026-03-01", "2026-03-31", paper);

        var w = Assert.Single(s.Warnings, x => x.Code == "overReturned");
        Assert.Equal(2m, w.Qty);
    }

    [Fact]
    public void An_unfinished_price_correction_is_flagged()
    {
        var prod = Guid.NewGuid();
        var paper = new Paper("RC-1") { CorrectionPending = true };
        var line = paper.Line(prod, rent: 5);
        paper.Issue(line, "2026-03-01", 10);

        var s = Run("2026-03-01", "2026-03-31", paper);

        var w = Assert.Single(s.Warnings, x => x.Code == "pricingCorrectionPending");
        Assert.Equal("RC-1", w.ContractNumber);
        Assert.Equal(paper.Id, w.ContractId);
    }

    [Fact]
    public void An_accessory_has_no_rent()
    {
        var prodParent = Guid.NewGuid();
        var prodAccessory = Guid.NewGuid();
        var paper = new Paper("C1");
        var parent = paper.Line(prodParent, rent: 5);
        var accessory = paper.Line(prodAccessory, rent: 0, parent: parent);
        paper.Issue(parent, "2026-03-01", 10);
        paper.Issue(accessory, "2026-03-01", 10);

        var s = Run("2026-03-01", "2026-03-31", paper);

        var accessoryItem = s.Items.Single(i => i.ProductId == prodAccessory);
        Assert.True(accessoryItem.NoRent);
        Assert.Equal(0m, accessoryItem.Value);
        Assert.Empty(accessoryItem.Rates);
    }

    [Fact]
    public void Contract_status_is_read_as_of_To()
    {
        Assert.Equal("goodsBack", QuantityStatement.StatusAsOf(D("2026-03-10"), D("2026-04-05"), D("2026-03-31")));
        Assert.Equal("closed", QuantityStatement.StatusAsOf(D("2026-03-10"), D("2026-04-05"), D("2026-04-05")));
        Assert.Equal("open", QuantityStatement.StatusAsOf(D("2026-03-10"), D("2026-04-05"), D("2026-03-05")));
    }

    [Fact]
    public void The_summary_adds_up_the_items()
    {
        var prod1 = Guid.NewGuid();
        var prod2 = Guid.NewGuid();
        var paper = new Paper("C1");
        var line1 = paper.Line(prod1, rent: 5);
        var line2 = paper.Line(prod2, rent: 5);
        paper.Issue(line1, "2026-03-01", 10);
        paper.Issue(line2, "2026-03-01", 4);
        paper.Return(line1, "2026-03-10", ok: 4);

        var s = Run("2026-03-01", "2026-03-31", paper);

        Assert.Equal(2, s.Summary.ItemTypes);
        Assert.Equal(14m, s.Summary.Issued);
        Assert.Equal(4m, s.Summary.Returned);
        Assert.Equal(10m, s.Summary.WithCustomer);
        Assert.Equal(s.Items.Sum(i => i.Value), s.Summary.Value);
    }
}
