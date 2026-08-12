using System.Security.Claims;

namespace Magpilot.Hub.Auth;

/// <summary>
/// Multi-user agent visibility rules. Agents carry an
/// <c>OwnerUser</c> (the GitHub login that enrolled/adopted them);
/// the hub scopes what each caller can see and reach based on it.
///
/// Three caller shapes:
/// <list type="bullet">
///   <item><b>Infrastructure bearer</b> (<c>auth_kind = phone_bearer</c>):
///     a machine (preflight, sidecars, the MAUI app), not a person.
///     Unscoped -- sees and can reach every agent.</item>
///   <item><b>Admin browser user</b> (the first
///     <c>OAUTH_ALLOWED_GITHUB_USERS</c> entry): a superuser. Their
///     <em>main</em> agent list is still scoped to the agents they own
///     (so the day-to-day UI isn't cluttered with everyone's hosts),
///     but they get a dedicated all-agents admin view and can proxy to
///     any agent.</item>
///   <item><b>Regular browser user</b>: sees and can reach only the
///     agents they own.</item>
/// </list>
///
/// A <c>null</c> owner (rows enrolled before ownership existed, or
/// discovered-but-never-enrolled) is treated as belonging to the
/// admin's scoped bucket, so a fresh deployment's pre-existing agents
/// stay visible to the primary user without a data migration.
/// </summary>
public static class AgentVisibility
{
    /// <summary>The <c>auth_kind</c> claim value for the infra bearer scheme.</summary>
    public const string PhoneBearerKind = "phone_bearer";

    /// <summary>True for an infrastructure bearer caller (a machine, not a person).</summary>
    public static bool IsInfra(ClaimsPrincipal user) =>
        user.FindFirst("auth_kind")?.Value == PhoneBearerKind;

    /// <summary>
    /// True when <paramref name="login"/> is the configured admin
    /// (first allowed GitHub user). Case-insensitive.
    /// </summary>
    public static bool IsAdminLogin(string? login, HubAuthOptions opts) =>
        login is not null
        && opts.AdminUser is { } admin
        && string.Equals(login, admin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Superuser check: an infra bearer OR the admin login. Superusers
    /// see the all-agents view and can proxy/manage any agent.
    /// </summary>
    public static bool IsAdmin(ClaimsPrincipal user, HubAuthOptions opts) =>
        IsInfra(user) || IsAdminLogin(user.Identity?.Name, opts);

    /// <summary>
    /// Whether <paramref name="ownerUser"/> belongs in the
    /// <em>main scoped list</em> of a human browser user identified by
    /// <paramref name="login"/>. A matching owner is always included; a
    /// null owner is included only for the admin (so legacy / unowned
    /// agents surface for the primary user, nobody else). Note the
    /// admin does NOT see other users' agents here -- that's the whole
    /// point of the scoped main list; the all-agents view is separate.
    /// </summary>
    public static bool ScopedToOwner(string? ownerUser, string? login, bool isAdminLogin) =>
        ownerUser is { } o
            ? string.Equals(o, login, StringComparison.OrdinalIgnoreCase)
            : isAdminLogin;

    /// <summary>
    /// Whether the caller may proxy to / manage the agent. Admins
    /// (infra bearer or admin login) can reach any agent; everyone
    /// else only agents they own. A null owner is reachable only by an
    /// admin.
    /// </summary>
    public static bool CanAccess(string? ownerUser, string? login, bool isAdmin) =>
        isAdmin
        || (ownerUser is { } o && string.Equals(o, login, StringComparison.OrdinalIgnoreCase));
}
