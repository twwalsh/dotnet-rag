using SupportRag.Core.Retrieval;

namespace SupportRag.Tests;

public class ReciprocalRankFusionTests
{
    [Fact]
    public void An_item_in_both_lists_outranks_an_item_at_the_top_of_one()
    {
        var fused = ReciprocalRankFusion.Fuse([["a", "b", "c"], ["b", "d"]]);
        Assert.Equal("b", fused[0]);
        Assert.Equal(["b", "a", "d", "c"], fused);
    }

    [Fact]
    public void Ties_keep_first_appearance_order()
    {
        var fused = ReciprocalRankFusion.Fuse([["x"], ["y"]]);
        Assert.Equal(["x", "y"], fused);
    }

    [Fact]
    public void Empty_input_is_empty_output()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse([[], []]));
        Assert.Empty(ReciprocalRankFusion.Fuse([]));
    }

    [Fact]
    public void Duplicates_within_one_list_do_not_double_count_above_a_genuine_two_list_hit()
    {
        // "a" twice in one list (a bug upstream) must not beat "b" which both lists agree on.
        var fused = ReciprocalRankFusion.Fuse([["b", "a", "a"], ["b"]]);
        Assert.Equal("b", fused[0]);
    }
}
