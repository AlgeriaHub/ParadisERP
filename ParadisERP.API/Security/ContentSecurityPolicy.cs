using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ParadisERP.API.Security;

/// <summary>
/// The browser's security policy for every response (security review, 2026-10-10, L1). Until then
/// it said only <c>frame-ancestors 'none'</c>: with a session token in the browser's storage, any
/// script that ever got into a page could have read it and sent it anywhere.
///
/// <para>
/// Scripts only from this site, plus the one inline script <c>index.html</c> carries (the theme,
/// applied before the app boots), allowed by its hash — read from the published page at start-up,
/// so editing that script never needs this file changed. Connections only to this site and to the
/// error monitor (core/monitoring.ts), which also stops the sign-in screen's "server" field from
/// sending typed credentials to another host. Styles may be inline: Angular writes each
/// component's styles into a <c>&lt;style&gt;</c> element. Images may be <c>data:</c> and
/// <c>blob:</c>: the logos and the attachments are shown that way.
/// </para>
/// <para>
/// The Angular build must not inline its critical CSS (angular.json, <c>inlineCritical: false</c>):
/// that puts an <c>onload</c> handler on the stylesheet link, which this policy would block, and
/// the page would stay unstyled.
/// </para>
/// </summary>
public static partial class ContentSecurityPolicy
{
    /// <summary>Where the browser's error reports go (core/monitoring.ts).</summary>
    public const string ErrorMonitor = "https://*.ingest.de.sentry.io";

    public static string For(string? webRoot)
    {
        var index = webRoot is null ? null : Path.Combine(webRoot, "index.html");
        var scripts = new[] { "'self'" }.Concat(InlineScriptHashes(index).Select(h => $"'sha256-{h}'"));
        return string.Join("; ",
            "default-src 'self'",
            $"script-src {string.Join(' ', scripts)}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            $"connect-src 'self' {ErrorMonitor}",
            "worker-src 'self'",
            "manifest-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "frame-ancestors 'none'");
    }

    /// <summary>
    /// The SHA-256 of each inline script in the page, base64, as the browser computes it: on the
    /// element's text after parsing, where a CR LF pair has become one LF.
    /// </summary>
    public static IReadOnlyList<string> InlineScriptHashes(string? indexPath)
    {
        if (indexPath is null || !File.Exists(indexPath)) return [];
        var html = File.ReadAllText(indexPath, Encoding.UTF8);
        return InlineScript().Matches(html)
            .Select(m => m.Groups["body"].Value.Replace("\r\n", "\n").Replace('\r', '\n'))
            .Where(body => body.Trim().Length > 0)
            .Select(body => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))))
            .Distinct()
            .ToList();
    }

    [GeneratedRegex(@"<script(?![^>]*\bsrc\s*=)[^>]*>(?<body>.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();
}
