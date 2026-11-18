using System.Globalization;
using System.Net;
using System.Text.Json;
using ParadisERP.Application.Integrations;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Integrations;
using ParadisERP.Infrastructure.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Integrations;

public class IntegrationTests
{
    private static IntegrationSecretProtector Protector() => new(new EphemeralDataProtectionProvider());

    private static IntegrationSettings Webhook(string url = "https://books.example.com/hook") =>
        new() { Webhook = new WebhookSettings { Enabled = true, Url = url } };

    [Fact]
    public void Secrets_round_trip_and_unreadable_values_become_empty()
    {
        var protector = Protector();
        var stored = protector.Protect(new IntegrationSecrets { WebhookSecret = "super-secret-value", SmsToken = "tok" });
        Assert.DoesNotContain("super-secret-value", stored);
        Assert.Equal("super-secret-value", protector.Unprotect(stored).WebhookSecret);
        Assert.Null(protector.Unprotect("not-a-protected-payload").WebhookSecret);
        Assert.Null(Protector().Unprotect(stored).SmsToken);
    }

    [Fact]
    public void Secret_changes_keep_replace_or_clear()
    {
        var current = new IntegrationSecrets { SmsToken = "a", SmtpPassword = "b", WebhookSecret = "c" };
        var next = new SecretChanges { SmsToken = "", SmtpPassword = "new", ClearWebhookSecret = true }.ApplyTo(current);
        Assert.Equal("a", next.SmsToken);
        Assert.Equal("new", next.SmtpPassword);
        Assert.Null(next.WebhookSecret);
    }

    [Fact]
    public void Signature_is_hmac_sha256_hex() =>
        Assert.Equal("sha256=88aab3ede8d3adf94d26ab90d3bafd4a2083070c3bcce9c014ee04a443847c0b", WebhookSignature.Sign("secret", "hello"));

    [Theory]
    [InlineData("webhookWithoutSecret")]
    [InlineData("webhookShortSecret")]
    [InlineData("webhookFtp")]
    [InlineData("smsWithoutMessage")]
    [InlineData("smsBadHeader")]
    [InlineData("emailBadFrom")]
    [InlineData("emailUserWithoutPassword")]
    [InlineData("cardMode")]
    [InlineData("longSecret")]
    public void Invalid_settings_are_rejected(string problem)
    {
        var settings = new IntegrationSettings();
        var secrets = new IntegrationSecrets { WebhookSecret = "0123456789abcdef" };
        switch (problem)
        {
            case "webhookWithoutSecret": settings = Webhook(); secrets.WebhookSecret = null; break;
            case "webhookShortSecret": settings = Webhook(); secrets.WebhookSecret = "short"; break;
            case "webhookFtp": settings = Webhook("ftp://books.example.com"); break;
            case "smsWithoutMessage": settings.Sms = new() { Enabled = true, Url = "https://sms.example.com", BodyTemplate = "{\"to\":\"{to}\"}" }; break;
            case "smsBadHeader": settings.Sms = new() { Enabled = true, Url = "https://sms.example.com", TokenHeader = "X Bad:Header" }; break;
            case "emailBadFrom": settings.Email = new() { Enabled = true, Host = "smtp.example.com", From = "not-an-address" }; break;
            case "emailUserWithoutPassword": settings.Email = new() { Enabled = true, Host = "smtp.example.com", From = "shop@example.com", User = "shop" }; break;
            case "cardMode": settings.Card.Mode = "Stripe"; break;
            case "longSecret": secrets.SmsToken = new string('x', 501); break;
        }
        Assert.Throws<ArgumentException>(() => IntegrationSettingsValidator.NormalizeAndValidate(settings, secrets));
    }

    [Fact]
    public void Valid_settings_are_normalized()
    {
        var settings = new IntegrationSettings
        {
            Card = new() { Mode = "externalterminal", RequireCardReference = true },
            Sms = new() { Enabled = true, Url = " https://sms.example.com/send ", Method = "post" },
            Email = new() { Enabled = true, Host = "smtp.example.com", From = "shop@example.com" }
        };
        IntegrationSettingsValidator.NormalizeAndValidate(settings, new IntegrationSecrets());
        Assert.Equal(CardTerminalModes.ExternalTerminal, settings.Card.Mode);
        Assert.Equal("POST", settings.Sms.Method);
        Assert.Equal(new IntegrationsPublicView(CardTerminalModes.ExternalTerminal, true, true, true), settings.PublicView());

        var off = new IntegrationSettings { Card = new() { Mode = "None", RequireCardReference = true } };
        IntegrationSettingsValidator.NormalizeAndValidate(off, new IntegrationSecrets());
        Assert.False(off.PublicView().RequireCardReference);
    }

    [Theory]
    [InlineData("sms", "06 12-34.56 78", "0612345678")]
    [InlineData("sms", "+213 555 11 22 33", "+213555112233")]
    [InlineData("sms", "call me", null)]
    [InlineData("sms", "123", null)]
    [InlineData("email", " client@example.dz ", "client@example.dz")]
    [InlineData("email", "client@localhost", null)]
    [InlineData("email", "Name <client@example.dz>", null)]
    [InlineData("fax", "0612345678", null)]
    public void Recipients_are_validated(string channel, string to, string? expected) =>
        Assert.Equal(expected, IntegrationSettingsValidator.NormalizeRecipient(channel, to));

    private static (Sale Sale, Store Store) SampleSale() =>
    (
        new Sale
        {
            Reference = "C01-000042", SoldAtUtc = new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc),
            GrandTotalTtc = 1250m, TaxTotal = 199.58m, PaidAmount = 1300m, ChangeAmount = 50m, RoundingAdjustment = 0m,
            Lines = [new SaleLine { NameSnapshot = "Huile 5L", Qty = 2, LineTotalTtc = 1250m }]
        },
        new Store { NameFr = "Magasin Démo", NameEn = "Demo Store" }
    );

    [Fact]
    public void Receipt_text_dates_the_sale_the_same_under_any_server_culture()
    {
        // Under a Hijri server culture (ar-SA) the receipt read "1448-04-…" (found 2026-10-15).
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
        try
        {
            var (sale, store) = SampleSale();
            var text = ReceiptTextRenderer.Render(sale, store, RegionalPresets.Create("DZ")!);
            Assert.Contains("Ticket C01-000042 · 2026-09-17 11:00", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Receipt_text_uses_the_country_profile()
    {
        var (sale, store) = SampleSale();
        var algeria = RegionalPresets.Create("DZ")!;
        var text = ReceiptTextRenderer.Render(sale, store, algeria);
        Assert.Contains("Magasin Démo", text);
        Assert.Contains("Ticket C01-000042 · 2026-09-17 11:00", text);
        Assert.Contains("2 x Huile 5L  1 250,00 DA", text);
        Assert.Contains("Total: 1 250,00 DA (dont TVA 199,58 DA)", text);
        Assert.Contains("Rendu: 50,00 DA", text);
        Assert.EndsWith("Merci de votre visite", text);

        var saudi = RegionalPresets.Create("SA")!;
        saudi.DefaultLanguage = "en";
        saudi.CurrencySymbolFirst = true;
        sale.RoundingAdjustment = -0.05m;
        var english = ReceiptTextRenderer.Render(sale, store, saudi);
        Assert.Contains("Demo Store", english);
        Assert.Contains("Total: SAR 1,250.00", english);
        Assert.Contains("Rounding: SAR -0.05", english);
        Assert.Contains("Simplified tax invoice", english);
        Assert.Equal("Receipt C01-000042 — Demo Store", ReceiptTextRenderer.Subject(sale, store, saudi));
    }

    [Fact]
    public void A_sent_receipt_names_the_articles_as_the_catalogue_does_not_as_the_till_typed_them()
    {
        // Security review, 2026-10-10: the company's own sender carried whatever text the till pushed.
        var (sale, store) = SampleSale();
        var product = new Product { NameFr = "Huile de table 5 L", NameAr = "زيت المائدة 5 لتر" };
        sale.Lines[0].ProductId = product.Id;
        sale.Lines[0].NameSnapshot = "Gagnez 10 000 DA : http://example.com/win";
        var catalogue = new Dictionary<Guid, Product> { [product.Id] = product };

        var text = ReceiptTextRenderer.Render(sale, store, RegionalPresets.Create("DZ")!, catalogue);

        Assert.Contains("2 x Huile de table 5 L", text);
        Assert.DoesNotContain("example.com", text);
    }

    [Fact]
    public void Money_follows_currency_decimals()
    {
        var tunisia = RegionalPresets.Create("TN")!;
        Assert.Equal("1 234,568 DT", ReceiptTextRenderer.Money(1234.5675m, tunisia, "fr"));
    }

    [Fact]
    public void Sale_event_payload_carries_lines_and_payments()
    {
        var (sale, _) = SampleSale();
        sale.Payments = [new SalePayment { Method = PaymentMethod.Card, Amount = 1250m, Reference = "AUTH-77" }];
        using var doc = JsonDocument.Parse(SaleEvents.SaleAcceptedPayload("shop-1", sale));
        Assert.Equal("sale.accepted", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("shop-1", doc.RootElement.GetProperty("companyId").GetString());
        var payment = doc.RootElement.GetProperty("sale").GetProperty("payments")[0];
        Assert.Equal("Card", payment.GetProperty("method").GetString());
        Assert.Equal("AUTH-77", payment.GetProperty("reference").GetString());
    }

    [Fact]
    public void Backoff_grows_and_is_capped()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), SaleEvents.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(8), SaleEvents.Backoff(4));
        Assert.Equal(TimeSpan.FromHours(6), SaleEvents.Backoff(30));
    }

    [Fact]
    public void Receipt_sends_are_limited_per_sale()
    {
        var limiter = new ReceiptSendLimiter();
        var sale = Guid.NewGuid();
        for (var i = 0; i < ReceiptSendLimiter.MaxPerSale; i++) Assert.True(limiter.TryAcquire("a", sale));
        Assert.False(limiter.TryAcquire("a", sale));
        Assert.True(limiter.TryAcquire("b", sale));
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Sms_body_is_escaped_and_token_sent_in_the_header()
    {
        var recorder = new Recorder();
        var http = new IntegrationHttp(new Factory(recorder));
        var settings = new SmsGatewaySettings { Enabled = true, Url = "https://sms.example.com/send?to={to}", TokenHeader = "X-Api-Key" };
        var result = await http.SendSmsAsync(settings, "tok-1", "+213555", "Total \"1 250\"\nMerci", default);
        Assert.True(result.Ok);
        Assert.Equal("https://sms.example.com/send?to=%2B213555", recorder.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("tok-1", recorder.Request.Headers.GetValues("X-Api-Key").Single());
        var body = JsonDocument.Parse(recorder.Body!).RootElement;
        Assert.Equal("Total \"1 250\"\nMerci", body.GetProperty("message").GetString());
        Assert.Equal("+213555", body.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Webhook_requests_are_signed()
    {
        var recorder = new Recorder();
        await new IntegrationHttp(new Factory(recorder)).PostWebhookAsync("https://books.example.com/hook", "secret", "test", "hello", default);
        Assert.Equal("sha256=88aab3ede8d3adf94d26ab90d3bafd4a2083070c3bcce9c014ee04a443847c0b",
            recorder.Request!.Headers.GetValues(WebhookSignature.Header).Single());
        Assert.Equal("test", recorder.Request.Headers.GetValues("X-ParadisERP-Event").Single());
    }

    [Fact]
    public async Task Store_keeps_secrets_out_of_audit_and_checks_revisions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new PlatformIntegrationStore(db, Protector());

        const string secret = "webhook-signing-secret-1";
        Assert.True(await store.SaveAsync("shop", Webhook(), new SecretChanges { WebhookSecret = secret }, 0, "op", "Accounting bridge", default));
        Assert.False(await store.SaveAsync("shop", Webhook(), null, 0, "op", "Stale", default));
        Assert.True(await store.SaveAsync("shop", Webhook("https://books.example.com/v2"), new SecretChanges(), 1, "op", "New URL", default));

        var snapshot = await store.ReadAsync("shop", default);
        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(secret, snapshot.Secrets.WebhookSecret);
        Assert.Equal("https://books.example.com/v2", snapshot.Settings.Webhook.Url);
        Assert.All(await db.Audit.ToListAsync(), entry => Assert.DoesNotContain(secret, entry.Detail));
        Assert.DoesNotContain(secret, (await db.Integrations.SingleAsync()).ProtectedSecretsJson);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync("shop", Webhook(), new SecretChanges { ClearWebhookSecret = true }, 2, "op", "Remove secret", default));
    }
}
