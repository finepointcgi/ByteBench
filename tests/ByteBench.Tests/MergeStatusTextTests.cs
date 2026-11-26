using ByteBench.Services;
using ByteBench.Services.Git;

namespace ByteBench.Tests;

public class MergeStatusTextTests
{
    private static MergeGateResult Gate(bool blocked, bool bypass = false) =>
        new([], blocked ? ["2 approvals required"] : [], new EffectiveProtection([]), bypass);

    [Fact]
    public void Clean_and_unblocked_says_ready_to_merge()
    {
        Assert.Equal("Ready to merge. No conflicts.", MergeStatusText.Describe(MergeCheck.Clean, false, null, Gate(false), false));
        Assert.Equal("Ready to merge (can fast-forward).", MergeStatusText.Describe(MergeCheck.Clean, true, null, null, false));
        Assert.Equal(MergeStatusText.Level.Success, MergeStatusText.Severity(MergeCheck.Clean, Gate(false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Blocked_merge_never_says_ready_to_merge(bool canBypass)
    {
        foreach (var ff in new[] { false, true })
        {
            var text = MergeStatusText.Describe(MergeCheck.Clean, ff, null, Gate(true, canBypass), false);
            Assert.DoesNotContain("Ready to merge", text);
            Assert.StartsWith("No conflicts", text);
        }
        Assert.Equal(MergeStatusText.Level.Warning, MergeStatusText.Severity(MergeCheck.Clean, Gate(true, canBypass)));
    }

    [Fact]
    public void Conflicts_and_archived_keep_their_wording()
    {
        Assert.Contains("has conflicts", MergeStatusText.Describe(MergeCheck.Conflicts, false, null, Gate(true), false));
        Assert.Equal(MergeStatusText.Level.Error, MergeStatusText.Severity(MergeCheck.Conflicts, null));
        Assert.Contains("archived", MergeStatusText.Describe(MergeCheck.Clean, false, null, Gate(false), true));
    }
}
