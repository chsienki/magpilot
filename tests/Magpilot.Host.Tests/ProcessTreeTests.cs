using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class ProcessTreeTests
{
    [Fact]
    public void Self_counts_as_self_or_descendant()
    {
        Assert.True(ProcessTree.IsSelfOrDescendant(5, 5, new Dictionary<int, int>()));
    }

    [Fact]
    public void Grandchild_is_a_descendant_through_the_parent_chain()
    {
        // 100 (magpilot) -> 200 (agency) -> 300 (copilot)
        var parents = new Dictionary<int, int> { [300] = 200, [200] = 100, [100] = 0 };

        Assert.True(ProcessTree.IsSelfOrDescendant(300, 200, parents));  // copilot under agency
        Assert.True(ProcessTree.IsSelfOrDescendant(300, 100, parents));  // copilot under magpilot
    }

    [Fact]
    public void Unrelated_pid_is_not_a_descendant()
    {
        var parents = new Dictionary<int, int> { [300] = 200, [200] = 100, [999] = 1 };

        Assert.False(ProcessTree.IsSelfOrDescendant(999, 200, parents));
    }

    [Fact]
    public void A_cycle_in_the_map_terminates_instead_of_hanging()
    {
        var parents = new Dictionary<int, int> { [1] = 2, [2] = 1 };

        Assert.False(ProcessTree.IsSelfOrDescendant(1, 42, parents));
    }
}
