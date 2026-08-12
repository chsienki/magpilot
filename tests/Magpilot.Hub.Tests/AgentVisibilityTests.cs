using System.Security.Claims;
using Magpilot.Hub.Auth;
using Xunit;

namespace Magpilot.Hub.Tests;

/// <summary>
/// Multi-user agent visibility rules. These pin the three caller
/// shapes (infra bearer / admin login / regular user) and the two
/// distinct decisions (main scoped-list membership vs. proxy access),
/// plus the null-owner-belongs-to-admin fallback that keeps a fresh
/// deployment's pre-existing agents visible without a data migration.
/// </summary>
public sealed class AgentVisibilityTests
{
    private const string Admin = "chsienki";
    private const string Alice = "alice";

    private static HubAuthOptions Opts(params string[] allowed) =>
        new(PhoneBearer: "bearer", AllowedGitHubUsers: allowed,
            OAuthClientId: null, OAuthClientSecret: null, CookieDomain: null);

    private static ClaimsPrincipal User(string? name, string authKind)
    {
        var claims = new List<Claim> { new("auth_kind", authKind) };
        if (name is not null) claims.Add(new Claim(ClaimTypes.Name, name));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static ClaimsPrincipal OAuthUser(string name) => User(name, "github_oauth");
    private static ClaimsPrincipal BearerUser() => User(Admin, AgentVisibility.PhoneBearerKind);

    // ---- HubAuthOptions.AdminUser ----------------------------------------

    [Fact]
    public void AdminUser_IsFirstAllowedUser()
    {
        Assert.Equal(Admin, Opts(Admin, Alice).AdminUser);
    }

    [Fact]
    public void AdminUser_IsNull_WhenAllowlistEmpty()
    {
        Assert.Null(Opts().AdminUser);
    }

    // ---- IsInfra ---------------------------------------------------------

    [Fact]
    public void IsInfra_True_ForPhoneBearer()
    {
        Assert.True(AgentVisibility.IsInfra(BearerUser()));
    }

    [Fact]
    public void IsInfra_False_ForOAuthUser()
    {
        Assert.False(AgentVisibility.IsInfra(OAuthUser(Admin)));
    }

    // ---- IsAdminLogin ----------------------------------------------------

    [Theory]
    [InlineData("chsienki", true)]
    [InlineData("CHSIENKI", true)]  // case-insensitive
    [InlineData("alice", false)]
    [InlineData(null, false)]
    public void IsAdminLogin_MatchesFirstAllowedUserCaseInsensitively(string? login, bool expected)
    {
        Assert.Equal(expected, AgentVisibility.IsAdminLogin(login, Opts(Admin, Alice)));
    }

    // ---- IsAdmin (superuser) ---------------------------------------------

    [Fact]
    public void IsAdmin_True_ForInfraBearer()
    {
        Assert.True(AgentVisibility.IsAdmin(BearerUser(), Opts(Admin, Alice)));
    }

    [Fact]
    public void IsAdmin_True_ForAdminLogin()
    {
        Assert.True(AgentVisibility.IsAdmin(OAuthUser(Admin), Opts(Admin, Alice)));
    }

    [Fact]
    public void IsAdmin_False_ForRegularUser()
    {
        Assert.False(AgentVisibility.IsAdmin(OAuthUser(Alice), Opts(Admin, Alice)));
    }

    // ---- ScopedToOwner (main scoped list) --------------------------------

    [Fact]
    public void ScopedToOwner_True_WhenOwnerMatches()
    {
        Assert.True(AgentVisibility.ScopedToOwner(Alice, Alice, isAdminLogin: false));
    }

    [Fact]
    public void ScopedToOwner_CaseInsensitiveOwnerMatch()
    {
        Assert.True(AgentVisibility.ScopedToOwner("ALICE", Alice, isAdminLogin: false));
    }

    [Fact]
    public void ScopedToOwner_False_ForForeignOwner_EvenForAdmin()
    {
        // The whole point of the scoped main list: the admin does NOT
        // see other users' agents here. That's the /admin/agents/all view.
        Assert.False(AgentVisibility.ScopedToOwner(Alice, Admin, isAdminLogin: true));
    }

    [Fact]
    public void ScopedToOwner_NullOwner_VisibleToAdminOnly()
    {
        Assert.True(AgentVisibility.ScopedToOwner(null, Admin, isAdminLogin: true));
        Assert.False(AgentVisibility.ScopedToOwner(null, Alice, isAdminLogin: false));
    }

    // ---- CanAccess (proxy / management gate) -----------------------------

    [Fact]
    public void CanAccess_Admin_ReachesAnyAgent()
    {
        Assert.True(AgentVisibility.CanAccess(Alice, Admin, isAdmin: true));   // foreign-owned
        Assert.True(AgentVisibility.CanAccess(null, Admin, isAdmin: true));    // unowned
    }

    [Fact]
    public void CanAccess_RegularUser_OnlyOwnAgents()
    {
        Assert.True(AgentVisibility.CanAccess(Alice, Alice, isAdmin: false));  // own
        Assert.False(AgentVisibility.CanAccess(Admin, Alice, isAdmin: false)); // someone else's
        Assert.False(AgentVisibility.CanAccess(null, Alice, isAdmin: false));  // unowned
    }

    [Fact]
    public void CanAccess_CaseInsensitiveOwnerMatch()
    {
        Assert.True(AgentVisibility.CanAccess("ALICE", Alice, isAdmin: false));
    }
}
