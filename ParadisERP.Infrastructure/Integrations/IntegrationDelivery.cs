using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParadisERP.Application.Integrations;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;

namespace ParadisERP.Infrastructure.Integrations;

public static class WebhookSignature
{
    public const string Header = "X-ParadisERP-Signature";

    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}

public sealed record DeliveryResult(bool Ok, string? Error)
{
    public static readonly DeliveryResult Success = new(true, null);
}

/// <summary>Outbound HTTP for webhooks and SMS; 10-second timeout, never follows redirects.</summary>
public sealed class IntegrationHttp(IHttpClientFactory factory)
{
    public const string ClientName = "integrations";

    public async Task<DeliveryResult> PostWebhookAsync(string url, string secret, string eventType, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WebhookSignature.Header, WebhookSignature.Sign(secret, body));
        request.Headers.Add("X-ParadisERP-Event", eventType);
        return await SendAsync(request, ct);
    }

    public async Task<DeliveryResult> SendSmsAsync(SmsGatewaySettings settings, string? token, string to, string message, CancellationToken ct)
    {
        var json = settings.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        string Body(string value) => json ? JsonEncodedText.Encode(value).ToString() : Uri.EscapeDataString(value);
        var url = settings.Url.Replace("{to}", Uri.EscapeDataString(to)).Replace("{message}", Uri.EscapeDataString(message));
        using var request = new HttpRequestMessage(new HttpMethod(settings.Method), url);
        if (settings.Method != "GET")
            request.Content = new StringContent(
                settings.BodyTemplate.Replace("{to}", Body(to)).Replace("{message}", Body(message)), Encoding.UTF8);
        if (request.Content is not null)
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(settings.ContentType);
        if (!string.IsNullOrEmpty(token) && settings.TokenHeader.Length > 0)
            request.Headers.TryAddWithoutValidation(settings.TokenHeader, token);
        return await SendAsync(request, ct);
    }

    private async Task<DeliveryResult> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await factory.CreateClient(ClientName).SendAsync(request, ct);
            return response.IsSuccessStatusCode ? DeliveryResult.Success : new(false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, ex is TaskCanceledException ? "Timed out." : ex.Message);
        }
    }
}

public sealed class SmtpReceiptSender
{
    public async Task<DeliveryResult> SendAsync(SmtpSettings settings, string? password, string to, string subject, string body, CancellationToken ct)
    {
        using var message = new MailMessage(settings.From, to, subject, body) { BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.UseTls, Timeout = 10_000 };
        if (settings.User.Length > 0) client.Credentials = new NetworkCredential(settings.User, password);
        try
        {
            await client.SendMailAsync(message, ct);
            return DeliveryResult.Success;
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or IOException)
        {
            return new(false, ex.Message);
        }
    }
}

/// <summary>At most a few sends per sale, so a till cannot be used to spam a number or address.</summary>
public sealed class ReceiptSendLimiter
{
    public const int MaxPerSale = 5;
    private readonly ConcurrentDictionary<string, int> counts = new();

    public bool TryAcquire(string companyId, Guid saleId)
    {
        var key = companyId + "/" + saleId;
        var count = counts.AddOrUpdate(key, 1, (_, current) => current + 1);
        return count <= MaxPerSale;
    }
}

/// <summary>Plain-text receipt for SMS and e-mail, in the company's default language.</summary>
public static class ReceiptTextRenderer
{
    private static readonly Dictionary<string, Dictionary<string, string>> Labels = new()
    {
        ["fr"] = new() { ["receipt"] = "Ticket", ["refund"] = "Remboursement", ["total"] = "Total", ["tax"] = "dont", ["paid"] = "Payé", ["change"] = "Rendu", ["rounding"] = "Arrondi", ["thanks"] = "Merci de votre visite" },
        ["en"] = new() { ["receipt"] = "Receipt", ["refund"] = "Refund", ["total"] = "Total", ["tax"] = "incl.", ["paid"] = "Paid", ["change"] = "Change", ["rounding"] = "Rounding", ["thanks"] = "Thank you for your visit" },
        ["ar"] = new() { ["receipt"] = "وصل", ["refund"] = "استرجاع", ["total"] = "المجموع", ["tax"] = "منها", ["paid"] = "المدفوع", ["change"] = "الباقي", ["rounding"] = "تقريب", ["thanks"] = "شكراً لزيارتكم" },
    };

    public static string Subject(Sale sale, Store store, RegionalSettings regional)
    {
        var l = LabelsFor(regional);
        return $"{l["receipt"]} {sale.Reference} — {StoreName(store, regional)}";
    }

    /// <param name="catalogue">
    /// The articles of the sale as the catalogue names them, by id (security review, 2026-10-10):
    /// the line's snapshot is text the till sent, and a receipt sent from the company's own SMS
    /// sender or mailbox could carry whatever a cashier typed there — a link, a phishing line. The
    /// snapshot is printed only for an article the catalogue no longer has.
    /// </param>
    public static string Render(Sale sale, Store store, RegionalSettings regional,
        IReadOnlyDictionary<Guid, Product>? catalogue = null)
    {
        var l = LabelsFor(regional);
        var lang = LanguageOf(regional);
        var text = new StringBuilder();
        text.AppendLine(StoreName(store, regional));
        if (sale.Status == SaleStatus.Refunded) text.AppendLine(l["refund"]);
        text.AppendLine($"{l["receipt"]} {sale.Reference} · {LocalTime(sale.SoldAtUtc, regional).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        foreach (var line in sale.Lines)
            text.AppendLine($"{line.Qty.ToString("0.###", CultureInfo.InvariantCulture)} x {LineName(line, catalogue, lang)}  {Money(line.LineTotalTtc, regional, lang)}");
        text.AppendLine($"{l["total"]}: {Money(sale.GrandTotalTtc, regional, lang)} ({l["tax"]} {Text(regional.TaxNames, lang)} {Money(sale.TaxTotal, regional, lang)})");
        if (sale.RoundingAdjustment != 0)
            text.AppendLine($"{l["rounding"]}: {Money(sale.RoundingAdjustment, regional, lang)}");
        text.AppendLine($"{l["paid"]}: {Money(sale.PaidAmount, regional, lang)}");
        if (sale.ChangeAmount > 0) text.AppendLine($"{l["change"]}: {Money(sale.ChangeAmount, regional, lang)}");
        var legal = Text(regional.ReceiptLegalText, lang);
        if (legal.Length > 0) text.AppendLine(legal);
        text.Append(string.IsNullOrWhiteSpace(store.ReceiptFooter) ? l["thanks"] : store.ReceiptFooter);
        return text.ToString();
    }

    /// <summary>Formats with the currency's decimals and symbol: "1 250,00 DA", "€ 12.50".</summary>
    public static string Money(decimal value, RegionalSettings regional, string lang)
    {
        var format = new NumberFormatInfo
        {
            NumberDecimalSeparator = lang == "en" ? "." : ",",
            NumberGroupSeparator = lang == "en" ? "," : " ",
            NegativeSign = "-"
        };
        var amount = regional.RoundMoney(value).ToString("N" + regional.CurrencyDecimals, format);
        var symbol = Text(regional.CurrencySymbols, lang);
        if (symbol.Length == 0) symbol = regional.CurrencyCode;
        return regional.CurrencySymbolFirst ? $"{symbol} {amount}" : $"{amount} {symbol}";
    }

    private static string LanguageOf(RegionalSettings regional) =>
        Labels.ContainsKey(regional.DefaultLanguage) ? regional.DefaultLanguage : "fr";

    private static Dictionary<string, string> LabelsFor(RegionalSettings regional) => Labels[LanguageOf(regional)];

    private static string Text(Dictionary<string, string> values, string lang) =>
        values.GetValueOrDefault(lang) ?? values.GetValueOrDefault("fr") ?? values.Values.FirstOrDefault() ?? "";

    private static string LineName(SaleLine line, IReadOnlyDictionary<Guid, Product>? catalogue, string lang) =>
        catalogue?.GetValueOrDefault(line.ProductId) is not Product product ? line.NameSnapshot : lang switch
        {
            "ar" when product.NameAr.Length > 0 => product.NameAr,
            "en" when product.NameEn.Length > 0 => product.NameEn,
            _ => product.NameFr.Length > 0 ? product.NameFr : line.NameSnapshot
        };

    private static string StoreName(Store store, RegionalSettings regional) => LanguageOf(regional) switch
    {
        "ar" when store.NameAr.Length > 0 => store.NameAr,
        "en" when store.NameEn.Length > 0 => store.NameEn,
        _ => store.NameFr.Length > 0 ? store.NameFr : store.NameEn
    };

    private static DateTime LocalTime(DateTime utc, RegionalSettings regional)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(regional.TimeZone)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return utc; }
    }
}

public static class SaleEvents
{
    public const string SaleAccepted = "sale.accepted";
    public const string Test = "test";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string SaleAcceptedPayload(string companyId, Sale sale) => JsonSerializer.Serialize(new
    {
        type = SaleAccepted,
        companyId,
        sale = new
        {
            sale.Id, sale.Reference, sale.DeviceCode, sale.BranchId, sale.SoldAtUtc, sale.ReceivedAtUtc,
            sale.CustomerId, sale.CashierName, sale.SubTotalHt, sale.TaxTotal, sale.DiscountTotal,
            sale.GrandTotalTtc, sale.RoundingAdjustment, sale.PaidAmount, sale.ChangeAmount,
            paymentMethod = sale.PaymentMethod.ToString(), status = sale.Status.ToString(), sale.RefundOfSaleId,
            lines = sale.Lines.Select(l => new { l.ProductId, l.Sku, l.NameSnapshot, l.Qty, l.UnitPrice, l.TaxRate, l.LineDiscount, l.LineTotalTtc, l.LineTaxAmount }),
            payments = sale.Payments.Select(p => new { method = p.Method.ToString(), p.Amount, p.Reference })
        }
    }, Json);

    public static string TestPayload(string companyId) =>
        JsonSerializer.Serialize(new { type = Test, companyId, sentAtUtc = DateTime.UtcNow }, Json);

    /// <summary>1, 2, 4 … minutes, capped at six hours.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(0, attempts - 1)), 360));
}
