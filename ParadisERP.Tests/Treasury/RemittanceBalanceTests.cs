using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Treasury;

[Collection(HostCollection.Name)]
public class RemittanceBalanceTests
{
    [Theory]
    [InlineData(100, 101, false)]
    [InlineData(0, 1, false)]
    [InlineData(-10, 1, false)]
    [InlineData(100, 100, true)]
    [InlineData(100, 40, true)]
    public async Task Transfer_checks_available_cash_before_writing(decimal balance, decimal amount, bool accepted)
    {
        using var host = new PosHost();
        using var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01"
        });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);
        var fromBranch = new Branch { NameFr = "Source test" };
        var toBranch = new Branch { NameFr = "Destination test" };
        var from = new CashBox { BranchId = fromBranch.Id, NameFr = "Source test" };
        var to = new CashBox { BranchId = toBranch.Id, NameFr = "Destination test" };
        db.Branches.AddRange(fromBranch, toBranch);
        db.CashBoxes.AddRange(from, to);
        db.CashMovements.Add(new CashMovement
        {
            CashBoxId = from.Id, Direction = balance < 0 ? CashDirection.Out : CashDirection.In,
            Amount = Math.Abs(balance), BalanceAfter = balance, Kind = CashMovementKind.Opening
        });
        await db.SaveChangesAsync();
        var entriesBefore = await db.LedgerEntries.CountAsync();
        var response = await client.PostAsJsonAsync("/api/treasury/remittances", new
        {
            fromCashBoxId = from.Id, toCashBoxId = to.Id, amount, reference = "BALANCE-TEST", note = (string?)null
        });
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        var movements = await db.CashMovements.AsNoTracking().Where(m => m.CashBoxId == from.Id || m.CashBoxId == to.Id).ToListAsync();
        Assert.Equal(accepted ? 3 : 1, movements.Count);
        Assert.Equal(entriesBefore + (accepted ? 1 : 0), await db.LedgerEntries.CountAsync());
        Assert.Equal(balance - (accepted ? amount : 0), movements.Where(m => m.CashBoxId == from.Id)
            .Sum(m => m.Direction == CashDirection.In ? m.Amount : -m.Amount));
        Assert.Equal(accepted ? amount : 0, movements.Where(m => m.CashBoxId == to.Id)
            .Sum(m => m.Direction == CashDirection.In ? m.Amount : -m.Amount));
        if (!accepted)
            Assert.Contains("available cash box balance", await response.Content.ReadAsStringAsync());
    }
}
