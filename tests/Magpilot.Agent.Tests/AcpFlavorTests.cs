using Magpilot.Agent.Acp;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Locks the split between session-scoped ACP configuration and process-scoped
/// child flavoring. Models/reasoning share the default multiplexed process;
/// disabling MCP servers changes the process key and command line.
/// </summary>
public sealed class AcpFlavorTests
{
    [Fact]
    public void ForModel_keeps_model_and_reasoning_off_the_process_command_line()
    {
        var flavor = AcpFlavor.ForModel("gpt-5.6-sol-fast", "none");

        Assert.Equal(AcpFlavor.Default.Key, flavor.Key);
        Assert.Equal(AcpFlavor.Default.Args, flavor.Args);
        Assert.DoesNotContain("--model", flavor.Args);
        Assert.DoesNotContain("--reasoning-effort", flavor.Args);
        Assert.Equal("gpt-5.6-sol-fast", flavor.Model);
        Assert.Equal("none", flavor.ReasoningEffort);
    }

    [Fact]
    public void Disabled_mcp_servers_append_flags_and_scope_the_process_key()
    {
        var plain = AcpFlavor.ForModel("gpt-5.6-sol-fast", "none");
        var scoped = AcpFlavor.ForModel("gpt-5.6-sol-fast", "none", ["home-assistant", "tunebase"]);

        Assert.Contains("--disable-mcp-server home-assistant", scoped.Args);
        Assert.Contains("--disable-mcp-server tunebase", scoped.Args);
        Assert.NotEqual(plain.Key, scoped.Key);
        Assert.Equal(["home-assistant", "tunebase"], scoped.DisabledMcpServers);
    }

    [Fact]
    public void Different_models_share_the_same_process_flavor()
    {
        var main = AcpFlavor.ForModel("claude-opus-4.8", null);
        var operations = AcpFlavor.ForModel("gpt-5.6-sol-fast", null);

        Assert.Equal(AcpFlavor.Default.Key, main.Key);
        Assert.Equal(main.Key, operations.Key);
        Assert.Equal(main.Args, operations.Args);
    }

    [Fact]
    public void ForModel_rejects_unsafe_server_names()
    {
        // A token with a space could inject an extra argument into the spawn.
        Assert.Throws<ArgumentException>(() =>
            AcpFlavor.ForModel("gpt-5.6-sol-fast", "none", ["ha --allow-all-paths"]));
    }

    [Fact]
    public void Resolve_threads_disable_to_a_scoped_flavor()
    {
        var f = AcpFlavor.Resolve(useAgency: false, model: "gpt-5.6-sol-fast",
            reasoningEffort: "none", disableMcpServers: ["tunebase"]);

        Assert.Contains("--disable-mcp-server tunebase", f.Args);
        Assert.Equal("gpt-5.6-sol-fast", f.Model);
        Assert.Equal("none", f.ReasoningEffort);
        Assert.Equal(["tunebase"], f.DisabledMcpServers);
    }

    [Fact]
    public void Resolve_preserves_reasoning_only_request_on_default_flavor()
    {
        var f = AcpFlavor.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: "none");

        Assert.Equal(AcpFlavor.Default.Key, f.Key);
        Assert.Null(f.Model);
        Assert.Equal("none", f.ReasoningEffort);
        Assert.DoesNotContain("--reasoning-effort", f.Args);
    }

    [Fact]
    public void Resolve_accepts_future_safe_reasoning_tokens_for_acp_to_validate()
    {
        var f = AcpFlavor.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: "future-level");

        Assert.Equal("future-level", f.ReasoningEffort);
    }
}
