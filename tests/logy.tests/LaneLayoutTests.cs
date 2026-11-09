using Logy.Core.Graph;

namespace Logy.Tests;

public sealed class LaneLayoutTests
{
    private static readonly IReadOnlyDictionary<string, string> NoTips = new Dictionary<string, string>();

    private static Commit C(string hash, params string[] parents) => new(hash, parents, "a", 0, [], hash);

    [Fact]
    public void Linear_history_uses_one_slot()
    {
        var rows = LaneLayout.Build([C("c", "b"), C("b", "a"), C("a")], "main", NoTips);
        Assert.All(rows, r => Assert.Equal(0, r.CommitSlot));
        Assert.Equal(1, rows.Max(r => r.SlotCount));
    }

    [Fact]
    public void Branch_and_merge_uses_two_slots_with_merge_and_fork_segments()
    {
        // m merges f into main; f forks from base b.   m(b2, f) ; f(b) ; b2(b) ; b
        var rows = LaneLayout.Build([C("m", "b2", "f"), C("f", "b"), C("b2", "b"), C("b")], "main", NoTips);

        Assert.Equal(2, rows.Max(r => r.SlotCount));
        Assert.Contains(rows[0].Bottom, s => s.From == 0 && s.To == 1);       // merge commit links to slot 1
        var fork = rows[3];                                                    // b: both lanes join here
        Assert.Contains(fork.Top, s => s.From == 1 && s.To == fork.CommitSlot);
    }

    [Fact]
    public void Two_unmerged_branches_use_three_slots()
    {
        var rows = LaneLayout.Build([C("x", "b"), C("y", "b"), C("m", "b"), C("b")], "main", NoTips);
        Assert.Equal(3, rows.Max(r => r.SlotCount));
    }

    [Fact]
    public void Merged_branch_tip_is_coloured_by_its_name()
    {
        var tips = new Dictionary<string, string> { ["m"] = "main", ["f"] = "feature/x" };
        var rows = LaneLayout.Build([C("m", "b2", "f"), C("b2", "b"), C("f", "b"), C("b")], "main", tips);

        var f = rows.Single(r => r.Commit.Hash == "f");
        Assert.Equal("feature/x", f.CommitColorKey);
        Assert.Contains(rows[0].Bottom, s => s.ColorKey == "feature/x");
    }

    [Fact]
    public void Duplicate_parent_gives_one_lane()
    {
        var rows = LaneLayout.Build([C("m", "a", "a"), C("a")], "main", NoTips);
        Assert.Equal(1, rows.Max(r => r.SlotCount));
    }

    [Fact]
    public void ColorIndex_is_stable_and_main_is_zero()
    {
        Assert.Equal(0, LaneLayout.ColorIndex("main", "main", 8));
        Assert.Equal(LaneLayout.ColorIndex("feature/x", "main", 8), LaneLayout.ColorIndex("feature/x", "main", 8));
        Assert.InRange(LaneLayout.ColorIndex("feature/x", "main", 8), 1, 7);
        Assert.Equal(0, LaneLayout.ColorIndex("feature/x", "main", 1));
    }
}
