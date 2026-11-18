using ParadisERP.Application.Security;
using ParadisERP.Infrastructure.Authorization;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Auth;

public sealed class UserSessionValidator(PosDbContext db) : IUserSessionValidator
{
    public async Task<bool> IsValidAsync(Guid userId, long version, string role, string deviceCode,
        CancellationToken cancellationToken = default) =>
        await CheckAsync(userId, version, role, deviceCode, cancellationToken) is not null;

    /// <summary>
    /// Every request: the user row and its role in one statement, read now — never cached — so a
    /// deactivated, deleted or signed-out user is refused at once, and the authorization stamp is as
    /// fresh as the session check itself.
    /// </summary>
    public async Task<SessionCheck?> CheckAsync(Guid userId, long version, string role, string deviceCode,
        CancellationToken cancellationToken = default)
    {
        var row = await ServerAuthorization.SessionRowAsync(db, userId, cancellationToken);
        if (row is not { IsActive: true, IsDeleted: false } || row.Version != version || row.Role.ToString() != role)
            return null;
        if (string.IsNullOrEmpty(deviceCode))
        {
            // An office session (no till). Re-checked on every request, so a role moved to a till
            // scope since sign-in loses it at once rather than at the token's expiry.
            if (row.Stamp is not { } stamp || !BranchScope.AllowsOfficeSession(stamp.Scope)) return null;
        }
        else if (!await db.Devices.AsNoTracking().AnyAsync(d => d.Code == deviceCode && d.IsActive, cancellationToken))
        {
            return null;
        }
        return new SessionCheck(row.Stamp);
    }
}
