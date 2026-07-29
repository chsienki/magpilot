using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class CopilotLaunchTests
{
    [Fact]
    public void BuildAgencyArgv_with_no_args_is_just_the_copilot_subcommand()
    {
        var argv = CopilotLaunch.BuildAgencyArgv([]);

        Assert.Equal(["copilot"], argv);
    }

    [Fact]
    public void BuildAgencyArgv_forwards_args_untouched_after_the_subcommand()
    {
        // No -- is injected: agency's own parser decides which args are
        // agency's (-a) and which pass through to copilot (--resume).
        var argv = CopilotLaunch.BuildAgencyArgv(["-a", "myagent", "--resume=abc"]);

        Assert.Equal(["copilot", "-a", "myagent", "--resume=abc"], argv);
    }

    [Fact]
    public void BuildAgencyArgv_preserves_a_user_supplied_double_dash()
    {
        // A user can still force the rest to copilot with their own --.
        var argv = CopilotLaunch.BuildAgencyArgv(["--", "--resume=abc"]);

        Assert.Equal(["copilot", "--", "--resume=abc"], argv);
    }

    [Fact]
    public void Parse_recognizes_agency_flag_and_strips_it_from_forward_args()
    {
        var opts = WrapperOptions.Parse(["--magpilot-agency", "--resume=abc"]);

        Assert.True(opts.Agency);
        Assert.Equal(["--resume=abc"], opts.ForwardArgs);
    }

    [Fact]
    public void Parse_defaults_agency_to_false()
    {
        var opts = WrapperOptions.Parse(["--resume=abc"]);

        Assert.False(opts.Agency);
    }
}
