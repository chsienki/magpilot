using Magpilot.Agent.Acp;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void Resolve_appends_process_arguments_in_canonical_order_and_preserves_copilot_home()
    {
        var copilotHome = Path.Combine(Path.GetTempPath(), "magpilot-phone-home");

        var flavor = AcpFlavor.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            disableMcpServers: ["tunebase", "home-assistant", "tunebase"],
            agent: "magnus-phone",
            availableTools: ["server(tool_z)", "magnus-phone", "magnus-phone"],
            disableBuiltinMcps: true,
            noCustomInstructions: true,
            copilotHome: copilotHome);

        Assert.Equal(
            AcpFlavor.Default.Args +
            " --disable-mcp-server home-assistant" +
            " --disable-mcp-server tunebase" +
            " --agent magnus-phone" +
            " --available-tools=magnus-phone" +
            " --available-tools=server(tool_z)" +
            " --disable-builtin-mcps" +
            " --no-custom-instructions",
            flavor.Args);
        Assert.Matches("^default:scope-[0-9a-f]{64}$", flavor.Key);
        Assert.Equal(["home-assistant", "tunebase"], flavor.DisabledMcpServers);
        Assert.Equal(["magnus-phone", "server(tool_z)"], flavor.AvailableTools);
        Assert.Equal(Path.GetFullPath(copilotHome), flavor.CopilotHome);
        Assert.DoesNotContain(copilotHome, flavor.Args);
    }

    [Fact]
    public void Resolve_canonicalizes_collection_order_for_the_process_key()
    {
        var first = AcpFlavor.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            disableMcpServers: ["tunebase", "home-assistant"],
            availableTools: ["server(tool_z)", "magnus-phone"]);
        var reordered = AcpFlavor.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            disableMcpServers: ["home-assistant", "tunebase", "home-assistant"],
            availableTools: ["magnus-phone", "server(tool_z)", "magnus-phone"]);

        Assert.Equal(first.Key, reordered.Key);
        Assert.Equal(first.Args, reordered.Args);
    }

    [Fact]
    public void Resolve_process_key_is_unambiguous_when_selector_text_resembles_scope_labels()
    {
        var builtinDisabled = AcpFlavor.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            availableTools: ["*"],
            disableBuiltinMcps: true);
        var selectorOnly = AcpFlavor.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            availableTools: ["*:no-builtin-mcps"]);

        Assert.NotEqual(builtinDisabled.Args, selectorOnly.Args);
        Assert.NotEqual(builtinDisabled.Key, selectorOnly.Key);
    }

    [Fact]
    public void Resolve_isolates_each_new_process_scope_setting()
    {
        var copilotHome = Path.Combine(Path.GetTempPath(), "magpilot-isolated-home");
        var flavors = new[]
        {
            AcpFlavor.Default,
            AcpFlavor.Resolve(false, null, null, agent: "magnus-phone"),
            AcpFlavor.Resolve(false, null, null, availableTools: ["magnus-phone"]),
            AcpFlavor.Resolve(false, null, null, disableBuiltinMcps: true),
            AcpFlavor.Resolve(false, null, null, noCustomInstructions: true),
            AcpFlavor.Resolve(false, null, null, copilotHome: copilotHome),
        };

        Assert.Equal(flavors.Length, flavors.Select(static flavor => flavor.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("magnus phone")]
    [InlineData("magnus-phone --yolo")]
    public void Resolve_rejects_unsafe_agent_names(string agent)
    {
        Assert.Throws<ArgumentException>(() =>
            AcpFlavor.Resolve(false, null, null, agent: agent));
    }

    [Theory]
    [InlineData("magnus phone")]
    [InlineData("server(tool);whoami")]
    public void Resolve_rejects_unsafe_tool_selectors(string selector)
    {
        Assert.Throws<ArgumentException>(() =>
            AcpFlavor.Resolve(false, null, null, availableTools: [selector]));
    }

    [Fact]
    public void Resolve_rejects_explicit_empty_tool_allowlist()
    {
        Assert.Throws<ArgumentException>(() =>
            AcpFlavor.ValidateAvailableToolsRequest([]));
    }

    [Fact]
    public void Resolve_rejects_blank_tool_selector()
    {
        Assert.Throws<ArgumentException>(() =>
            AcpFlavor.ValidateAvailableToolsRequest([" "]));
    }

    [Fact]
    public void Resolve_rejects_relative_copilot_home()
    {
        Assert.Throws<ArgumentException>(() =>
            AcpFlavor.Resolve(false, null, null, copilotHome: "relative/copilot-home"));
    }

    [Fact]
    public async Task Flavor_pool_rejects_missing_copilot_home_before_spawning_child()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-copilot-home-{Guid.NewGuid():N}");
        var flavor = AcpFlavor.Resolve(false, null, null, copilotHome: missing);
        var pool = new AcpFlavorPool(
            NullLoggerFactory.Instance,
            NullLogger<AcpFlavorPool>.Instance);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            pool.AcquireAsync(flavor, CancellationToken.None));

        Assert.Contains("does not exist or is not ready", ex.Message);
    }

    [Fact]
    public async Task Flavor_pool_rejects_custom_home_with_private_session_state()
    {
        var home = Path.Combine(Path.GetTempPath(), $"invalid-copilot-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, "session-state"));
        try
        {
            var flavor = AcpFlavor.Resolve(false, null, null, copilotHome: home);
            var pool = new AcpFlavorPool(
                NullLoggerFactory.Instance,
                NullLogger<AcpFlavorPool>.Instance);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                pool.AcquireAsync(flavor, CancellationToken.None));

            Assert.Contains("must link session-state", ex.Message);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
