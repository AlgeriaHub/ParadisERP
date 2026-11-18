using ParadisERP.Domain;

namespace ParadisERP.Application.Security;

/// <summary>
/// Where the user's authorization stands right now, read with the session check (AUTH-1 Step 3):
/// their role and its state, and the two markers that move whenever either changes. A decision
/// cached under a different stamp is never used.
/// </summary>
public sealed record AuthorizationStamp(Guid RoleId, long RoleVersion, long UserVersion, RoleScope Scope, bool RoleActive);

/// <summary>The session is valid. <see cref="Authorization"/> is null for a user who has no role.</summary>
public sealed record SessionCheck(AuthorizationStamp? Authorization);

/// <summary>Online session checks; offline till unlock remains a separate boundary.</summary>
public interface IUserSessionValidator
{
    Task<bool> IsValidAsync(Guid userId, long version, string role, string deviceCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same rules as <see cref="IsValidAsync"/>, in the same statement that reads the user's
    /// authorization stamp: null when the session is refused.
    /// </summary>
    Task<SessionCheck?> CheckAsync(Guid userId, long version, string role, string deviceCode,
        CancellationToken cancellationToken = default);
}
