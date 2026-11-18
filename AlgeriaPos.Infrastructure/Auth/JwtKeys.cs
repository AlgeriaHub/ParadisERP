namespace AlgeriaPos.Infrastructure.Auth;

/// <summary>
/// The signing keys written in this repository's own files (security review, 2026-10-10): the
/// Development default, docker-compose.yml's example and the deploy templates' marker. Long enough
/// to pass the length check, and known to anyone who has read the repository.
/// </summary>
public static class JwtKeys
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "dev-only-signing-key-change-me-please-32+chars",
        "change-this-to-a-long-random-secret-key-32+",
        "CHANGE-ME",
    };

    public static bool IsPublicPlaceholder(string? key) => key is not null && Placeholders.Contains(key.Trim());
}
