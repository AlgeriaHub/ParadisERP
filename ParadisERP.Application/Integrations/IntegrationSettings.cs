using System.Net.Mail;
using System.Text.RegularExpressions;

namespace ParadisERP.Application.Integrations;

public static class CardTerminalModes
{
    public const string None = "None";
    /// <summary>A standalone bank terminal (e.g. CIB/Edahabia via a SATIM-certified bank); the till only records the result.</summary>
    public const string ExternalTerminal = "ExternalTerminal";
    public static readonly string[] All = [None, ExternalTerminal];
}

public sealed class CardTerminalSettings
{
    public string Mode { get; set; } = CardTerminalModes.None;
    /// <summary>The cashier must type the terminal's approval/receipt number for card payments.</summary>
    public bool RequireCardReference { get; set; }
}

/// <summary>A generic HTTP SMS gateway. The token is a secret and is sent in <see cref="TokenHeader"/>.</summary>
public sealed class SmsGatewaySettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string TokenHeader { get; set; } = "Authorization";
    public string ContentType { get; set; } = "application/json";
    /// <summary>Request body; {to} and {message} are replaced (JSON-escaped when the content type is JSON).</summary>
    public string BodyTemplate { get; set; } = "{\"to\":\"{to}\",\"message\":\"{message}\"}";
}

public sealed class SmtpSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string User { get; set; } = "";
    public string From { get; set; } = "";
}

/// <summary>Accepted sales are posted here (accounting / e-invoicing bridge), signed with HMAC-SHA256.</summary>
public sealed class WebhookSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>Non-secret, operator-owned integration settings of one company.</summary>
public sealed class IntegrationSettings
{
    public CardTerminalSettings Card { get; set; } = new();
    public SmsGatewaySettings Sms { get; set; } = new();
    public SmtpSettings Email { get; set; } = new();
    public WebhookSettings Webhook { get; set; } = new();

    public IntegrationsPublicView PublicView() => new(
        Card.Mode, Card.Mode == CardTerminalModes.ExternalTerminal && Card.RequireCardReference,
        SmsReady(), EmailReady());

    public bool SmsReady() => Sms.Enabled && Sms.Url.Length > 0;
    public bool EmailReady() => Email.Enabled && Email.Host.Length > 0 && Email.From.Length > 0;
    public bool WebhookReady(IntegrationSecrets secrets) =>
        Webhook.Enabled && Webhook.Url.Length > 0 && !string.IsNullOrEmpty(secrets.WebhookSecret);
}

/// <summary>Write-only values; stored encrypted and never returned by any API.</summary>
public sealed class IntegrationSecrets
{
    public string? SmsToken { get; set; }
    public string? SmtpPassword { get; set; }
    public string? WebhookSecret { get; set; }
}

/// <summary>A secret change: a non-empty value replaces the stored one, <c>Clear</c> removes it, otherwise it is kept.</summary>
public sealed class SecretChanges
{
    public string? SmsToken { get; set; }
    public bool ClearSmsToken { get; set; }
    public string? SmtpPassword { get; set; }
    public bool ClearSmtpPassword { get; set; }
    public string? WebhookSecret { get; set; }
    public bool ClearWebhookSecret { get; set; }

    public IntegrationSecrets ApplyTo(IntegrationSecrets current) => new()
    {
        SmsToken = Next(current.SmsToken, SmsToken, ClearSmsToken),
        SmtpPassword = Next(current.SmtpPassword, SmtpPassword, ClearSmtpPassword),
        WebhookSecret = Next(current.WebhookSecret, WebhookSecret, ClearWebhookSecret)
    };

    private static string? Next(string? current, string? value, bool clear) =>
        clear ? null : string.IsNullOrEmpty(value) ? current : value;
}

/// <summary>What tills may know: no URLs, no secrets.</summary>
public sealed record IntegrationsPublicView(string CardMode, bool RequireCardReference, bool SmsReceipts, bool EmailReceipts)
{
    public static readonly IntegrationsPublicView None = new(CardTerminalModes.None, false, false, false);
}

public interface ICompanyIntegrationsProvider
{
    Task<(IntegrationSettings Settings, IntegrationSecrets Secrets)> GetAsync(string companyId, CancellationToken ct);
}

public static partial class IntegrationSettingsValidator
{
    public const int MaxSecretLength = 500;

    [GeneratedRegex(@"^\+?[0-9]{6,15}$")] private static partial Regex PhonePattern();
    [GeneratedRegex(@"^[A-Za-z0-9-]{1,60}$")] private static partial Regex HeaderPattern();

    public static void NormalizeAndValidate(IntegrationSettings s, IntegrationSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Card ??= new(); s.Sms ??= new(); s.Email ??= new(); s.Webhook ??= new();
        s.Card.Mode = CardTerminalModes.All.FirstOrDefault(m => m.Equals((s.Card.Mode ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Card mode must be one of: {string.Join(", ", CardTerminalModes.All)}.");
        if (s.Card.Mode == CardTerminalModes.None) s.Card.RequireCardReference = false;

        s.Sms.Url = (s.Sms.Url ?? "").Trim();
        s.Sms.Method = (s.Sms.Method ?? "").Trim().ToUpperInvariant();
        s.Sms.TokenHeader = (s.Sms.TokenHeader ?? "").Trim();
        s.Sms.ContentType = (s.Sms.ContentType ?? "").Trim();
        s.Sms.BodyTemplate ??= "";
        if (s.Sms.Enabled)
        {
            RequireHttpUrl(s.Sms.Url, "SMS gateway URL");
            if (s.Sms.Method is not ("POST" or "PUT" or "GET")) throw new ArgumentException("SMS method must be GET, POST or PUT.");
            if (s.Sms.TokenHeader.Length > 0 && !HeaderPattern().IsMatch(s.Sms.TokenHeader))
                throw new ArgumentException("The SMS token header name is invalid.");
            if (s.Sms.ContentType.Length is 0 or > 100) throw new ArgumentException("An SMS content type is required.");
            if (s.Sms.BodyTemplate.Length > 2000) throw new ArgumentException("The SMS body template is limited to 2000 characters.");
            if (!(s.Sms.BodyTemplate + s.Sms.Url).Contains("{message}")) throw new ArgumentException("The SMS URL or body must contain {message}.");
            if (!(s.Sms.BodyTemplate + s.Sms.Url).Contains("{to}")) throw new ArgumentException("The SMS URL or body must contain {to}.");
        }

        s.Email.Host = (s.Email.Host ?? "").Trim();
        s.Email.User = (s.Email.User ?? "").Trim();
        s.Email.From = (s.Email.From ?? "").Trim();
        if (s.Email.Enabled)
        {
            if (Uri.CheckHostName(s.Email.Host) == UriHostNameType.Unknown) throw new ArgumentException("A valid SMTP host is required.");
            if (s.Email.Port is < 1 or > 65535) throw new ArgumentException("The SMTP port must be between 1 and 65535.");
            if (!IsEmail(s.Email.From)) throw new ArgumentException("A valid sender address is required.");
            if (s.Email.User.Length > 200) throw new ArgumentException("The SMTP user is limited to 200 characters.");
            if (s.Email.User.Length > 0 && string.IsNullOrEmpty(secrets.SmtpPassword))
                throw new ArgumentException("An SMTP password is required when a user is set.");
        }

        s.Webhook.Url = (s.Webhook.Url ?? "").Trim();
        if (s.Webhook.Enabled)
        {
            RequireHttpUrl(s.Webhook.Url, "Webhook URL");
            if (string.IsNullOrEmpty(secrets.WebhookSecret) || secrets.WebhookSecret.Length < 16)
                throw new ArgumentException("A webhook signing secret of at least 16 characters is required.");
        }

        foreach (var value in new[] { secrets.SmsToken, secrets.SmtpPassword, secrets.WebhookSecret })
            if (value is { Length: > MaxSecretLength }) throw new ArgumentException($"Secrets are limited to {MaxSecretLength} characters.");
    }

    public static bool IsPhone(string value) => PhonePattern().IsMatch(value);

    public static bool IsEmail(string value)
    {
        if (value.Length is 0 or > 254 || value.Contains(' ')) return false;
        return MailAddress.TryCreate(value, out var address) && address.Address == value && value.Contains('.');
    }

    /// <summary>Normalizes a receipt recipient ("06 12 34 56 78" → "0612345678") or returns null when invalid.</summary>
    public static string? NormalizeRecipient(string channel, string? to)
    {
        var value = (to ?? "").Trim();
        if (channel == "sms")
        {
            value = value.Replace(" ", "").Replace("-", "").Replace(".", "");
            return IsPhone(value) ? value : null;
        }
        return channel == "email" && IsEmail(value) ? value : null;
    }

    private static void RequireHttpUrl(string value, string name)
    {
        if (value.Length > 500 || !Uri.TryCreate(value.Replace("{to}", "x").Replace("{message}", "x"), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"{name} must be an http(s) address.");
    }
}
