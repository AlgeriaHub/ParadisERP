using ParadiERP.Infrastructure.Auth;

namespace ParadiERP.Tests.Security;

/// <summary>The repository's own example keys never sign a live token (security review, 2026-10-10).</summary>
public class JwtKeysTests
{
    [Theory]
    [InlineData("dev-only-signing-key-change-me-please-32+chars")]
    [InlineData("change-this-to-a-long-random-secret-key-32+")]
    [InlineData(" CHANGE-ME ")]
    public void The_keys_written_in_the_repository_are_placeholders(string key) =>
        Assert.True(JwtKeys.IsPublicPlaceholder(key));

    [Theory]
    [InlineData("q8Vx2mL0r7Tz5Kc1Hn9Bw4Ys6Fd3Gp0Ja8Ue2Io7")]
    [InlineData(null)]
    public void A_key_of_one_s_own_is_not(string? key) =>
        Assert.False(JwtKeys.IsPublicPlaceholder(key));
}
