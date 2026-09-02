using Magpilot.Agent.Acp;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Locks the heuristic that drops copilot's "Info: &lt;path&gt;" tool-notices when
/// they bleed into the assistant message stream as standalone chunks. The risk
/// this guards is dropping a real assistant sentence, so the negative cases
/// (prose that merely starts with "Info: ") matter as much as the positive ones.
/// </summary>
public sealed class AcpInfoBleedTests
{
    [Theory]
    // Positive: a bare file-op notice with a path payload -> drop.
    [InlineData(@"Info: C:\Users\me\notes.txt", true)]
    [InlineData("Info: C:/Users/me/notes.txt", true)]
    [InlineData("Info: /home/magnus/notes.txt", true)]
    [InlineData(@"Info: D:\projects\magstronaut\README.md", true)]
    // Negative: real prose that happens to open with "Info: " -> keep.
    [InlineData("Info: the build succeeded and all tests pass.", false)]
    [InlineData("Info: 3 files changed.", false)]
    [InlineData("Info: Disabled tools can be re-enabled in settings.", false)]
    [InlineData("Information about the deployment follows.", false)]
    // Negative: not an Info notice at all -> keep.
    [InlineData(@"Here is C:\Users\me\notes.txt for reference.", false)]
    [InlineData("The file /etc/hosts controls name resolution.", false)]
    [InlineData("", false)]
    [InlineData("Info: ", false)]
    public void IsInfoPathBleed_matches_only_bare_path_notices(string text, bool expected)
    {
        Assert.Equal(expected, AcpSessionManager.IsInfoPathBleed(text));
    }

    [Theory]
    [InlineData("Info: Disabled tools:\n- shell\n- web", true, true)]
    [InlineData("Info: Disabled tools:\n- shell\n- web", false, false)]
    [InlineData("Info: Disabled tools:\n- shell\nThis is expected.", true, false)]
    [InlineData("Info: Disabled tools:", true, false)]
    public void IsInfoPathBleed_matches_disabled_tool_catalog_only_for_filtered_sessions(
        string text,
        bool toolFiltered,
        bool expected)
    {
        Assert.Equal(expected, AcpSessionManager.IsInfoPathBleed(text, toolFiltered));
    }
}
