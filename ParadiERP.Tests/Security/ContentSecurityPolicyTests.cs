using ParadiERP.API.Security;

namespace ParadiERP.Tests.Security;

/// <summary>
/// The browser's security policy (security review, 2026-10-10): scripts and connections only from
/// this site, the page's inline script allowed by the hash the browser computes for it.
/// </summary>
public class ContentSecurityPolicyTests
{
    [Fact]
    public void The_inline_script_is_allowed_by_its_hash_as_the_browser_computes_it_and_external_ones_are_not_hashed()
    {
        var root = Directory.CreateTempSubdirectory("csp-");
        try
        {
            // Saved with CR LF, as git leaves it on Windows: the browser hashes the parsed text, LF only.
            File.WriteAllText(Path.Combine(root.FullName, "index.html"),
                "<html><head><script>\r\n  var a = 1;\r\n</script>\r\n<script src=\"main.js\" type=\"module\"></script></head></html>");

            var policy = ContentSecurityPolicy.For(root.FullName);

            Assert.Contains("script-src 'self' 'sha256-wYD6gJuOAPxlJHFvhr2Zk0r9F+gmt8nwaf1uW8o5LTY='", policy);
            Assert.Single(ContentSecurityPolicy.InlineScriptHashes(Path.Combine(root.FullName, "index.html")));
            Assert.Contains($"connect-src 'self' {ContentSecurityPolicy.ErrorMonitor}", policy);
            Assert.Contains("object-src 'none'", policy);
            Assert.Contains("frame-ancestors 'none'", policy);
            Assert.DoesNotContain("unsafe-eval", policy);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Without_a_published_page_only_this_site_s_scripts_run()
    {
        Assert.Contains("script-src 'self';", ContentSecurityPolicy.For(null));
        Assert.Contains("script-src 'self';", ContentSecurityPolicy.For(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))));
    }
}
