using Magpilot.Host;
using Magpilot.Shared.Models;
using Xunit;

namespace Magpilot.Host.Tests;

public sealed class ResumeSessionPickerTests
{
    private const string FirstId = "11111111-1111-4111-8111-111111111111";
    private const string SecondId = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public void Selected_session_replaces_only_bare_resume()
    {
        var args = ResumeSessionPicker.WithSelectedSession(
            ["--model", "gpt-5.4", "--resume", "--allow-all-tools"],
            FirstId);

        Assert.Equal(
            ["--model", "gpt-5.4", $"--resume={FirstId}", "--allow-all-tools"],
            args);
        Assert.Equal(FirstId, WrapperOptions.Parse(args.ToArray()).ExtractKnownSessionId());
    }

    [Fact]
    public void Short_resume_alias_uses_the_same_selection_path()
    {
        var options = WrapperOptions.Parse(["-r", "--model", "gpt-5.4"]);

        Assert.Equal("", options.ExtractResumeSessionId());
        var args = ResumeSessionPicker.WithSelectedSession(options.ForwardArgs, FirstId);
        Assert.Equal([$"--resume={FirstId}", "--model", "gpt-5.4"], args);
        Assert.Equal(FirstId, WrapperOptions.Parse(args.ToArray()).ExtractKnownSessionId());
    }

    [Theory]
    [InlineData("--resume=some-name")]
    [InlineData("--resume=11111111-1111-4111-8111-111111111111")]
    public void Explicit_resume_is_not_replaced(string argument)
    {
        Assert.Throws<ArgumentException>(() =>
            ResumeSessionPicker.WithSelectedSession([argument], FirstId));
    }

    [Theory]
    [InlineData("--continue")]
    [InlineData("--session-id=22222222-2222-4222-8222-222222222222")]
    [InlineData("--resume=other")]
    [InlineData("-r")]
    public void Bare_resume_rejects_conflicting_session_selectors(string other)
    {
        Assert.Throws<ArgumentException>(() =>
            ResumeSessionPicker.WithSelectedSession(["--resume", other], FirstId));
    }

    [Fact]
    public void Current_directory_appears_before_newer_sessions_elsewhere()
    {
        var sessions = new[]
        {
            Session(SecondId, "newer elsewhere", "D:\\other", DateTimeOffset.UtcNow),
            Session(FirstId, "here", "D:\\work", DateTimeOffset.UtcNow.AddDays(-1)),
        };
        using var output = new StringWriter();

        var selected = ResumeSessionPicker.Choose(sessions, "d:\\work\\", new StringReader("1\n"), output);

        Assert.Equal(FirstId, selected?.Id);
        Assert.Contains("here", output.ToString());
    }

    [Theory]
    [InlineData("caching", FirstId)]
    [InlineData("D:\\other", SecondId)]
    [InlineData("/22222222", SecondId)]
    public void Search_finds_name_directory_and_numeric_id_prefix(string query, string expectedId)
    {
        var sessions = new[]
        {
            Session(FirstId, "Helix Caching", "D:\\work", DateTimeOffset.UtcNow),
            Session(SecondId, "Unrelated", "D:\\other", DateTimeOffset.UtcNow),
        };
        using var output = new StringWriter();

        var selected = ResumeSessionPicker.Choose(
            sessions, "D:\\work", new StringReader($"{query}\n1\n"), output);

        Assert.Equal(expectedId, selected?.Id);
        Assert.Contains("1 matching", output.ToString());
    }

    [Fact]
    public void Paging_can_select_sessions_beyond_first_page()
    {
        var sessions = Enumerable.Range(0, 10)
            .Select(i => Session(
                $"00000000-0000-4000-8000-{i:000000000000}",
                $"session {i}",
                "D:\\work",
                DateTimeOffset.UnixEpoch.AddMinutes(i)))
            .ToArray();
        using var output = new StringWriter();

        var selected = ResumeSessionPicker.Choose(
            sessions, "D:\\work", new StringReader("n\n2\n"), output);

        Assert.Equal("session 0", selected?.Summary);
        Assert.Contains("page 2/2", output.ToString());
    }

    [Fact]
    public void Cancel_and_empty_input_never_select_a_session()
    {
        var sessions = new[] { Session(FirstId, "one", "D:\\work", DateTimeOffset.UtcNow) };

        Assert.Null(ResumeSessionPicker.Choose(sessions, "D:\\work", new StringReader("q\n"), new StringWriter()));
        Assert.Null(ResumeSessionPicker.Choose(sessions, "D:\\work", new StringReader(""), new StringWriter()));
    }

    [Fact]
    public void Invalid_store_directories_are_not_offered()
    {
        var sessions = new[] { Session(".session-operation-locks", "internal", "D:\\work", DateTimeOffset.UtcNow) };
        using var output = new StringWriter();

        Assert.Null(ResumeSessionPicker.Choose(sessions, "D:\\work", new StringReader("1\n"), output));
        Assert.Contains("no Copilot sessions found", output.ToString());
    }

    [Fact]
    public void No_match_can_be_cleared_without_starting_a_different_session()
    {
        var sessions = new[] { Session(FirstId, "Helix Caching", "D:\\work", DateTimeOffset.UtcNow) };
        using var output = new StringWriter();

        var selected = ResumeSessionPicker.Choose(
            sessions, "D:\\work", new StringReader("not found\n1\n/\n1\n"), output);

        Assert.Equal(FirstId, selected?.Id);
        Assert.Contains("No matching sessions.", output.ToString());
        Assert.Contains("choose a displayed number", output.ToString());
    }

    private static SessionInfo Session(
        string id, string summary, string cwd, DateTimeOffset updatedAt) =>
        new(id, SessionState.Dormant, cwd, null, null, summary, null, null, updatedAt);
}
