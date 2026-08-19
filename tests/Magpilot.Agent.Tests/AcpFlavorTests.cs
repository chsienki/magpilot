using Magpilot.Agent.Acp;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Locks the model flavor's tool-scoping contract: disabling MCP servers must
/// land as <c>--disable-mcp-server</c> args AND change the flavor key, so the
/// pool hands a scoped session its own child rather than the full-tool one. The
/// phone router relies on this to shed the cast/home-automation tools it must
/// relay. Names are interpolated into the child command line, so unsafe tokens
/// must be rejected.
/// </summary>
public sealed class AcpFlavorTests
{
    [Fact]
    public void ForModel_appends_disable_flags_and_scopes_the_key()
    {
        var plain = AcpFlavor.ForModel("gpt-5.4-mini", "minimal");
        var scoped = AcpFlavor.ForModel("gpt-5.4-mini", "minimal", ["home-assistant", "tunebase"]);

        Assert.Contains("--model gpt-5.4-mini", scoped.Args);
        Assert.Contains("--reasoning-effort minimal", scoped.Args);
        Assert.Contains("--disable-mcp-server home-assistant", scoped.Args);
        Assert.Contains("--disable-mcp-server tunebase", scoped.Args);
        // A scoped child must not be pooled as the same process as the full one.
        Assert.NotEqual(plain.Key, scoped.Key);
    }

    [Fact]
    public void ForModel_without_disable_matches_the_plain_flavor()
    {
        var plain = AcpFlavor.ForModel("gpt-5.4-mini", "minimal");
        var nullDisable = AcpFlavor.ForModel("gpt-5.4-mini", "minimal", null);
        var emptyDisable = AcpFlavor.ForModel("gpt-5.4-mini", "minimal", []);

        Assert.Equal(plain.Key, nullDisable.Key);
        Assert.Equal(plain.Key, emptyDisable.Key);
        Assert.DoesNotContain("--disable-mcp-server", plain.Args);
    }

    [Fact]
    public void ForModel_rejects_unsafe_server_names()
    {
        // A token with a space could inject an extra argument into the spawn.
        Assert.Throws<ArgumentException>(() =>
            AcpFlavor.ForModel("gpt-5.4-mini", "minimal", ["ha --allow-all-paths"]));
    }

    [Fact]
    public void Resolve_threads_disable_to_the_model_flavor()
    {
        var f = AcpFlavor.Resolve(useAgency: false, model: "gpt-5.4-mini",
            reasoningEffort: "minimal", disableMcpServers: ["tunebase"]);

        Assert.Contains("--disable-mcp-server tunebase", f.Args);
    }
}
