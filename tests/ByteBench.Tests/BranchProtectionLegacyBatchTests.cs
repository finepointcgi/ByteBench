using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Tests.Infrastructure;

namespace ByteBench.Tests;

/// <summary>Rules saved before the merge queue existed have a stored batch size of 0; editing them must not need a queue.</summary>
public class BranchProtectionLegacyBatchTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task A_rule_with_a_zero_batch_size_saves_while_the_queue_is_off_but_not_when_it_is_on()
    {
        var project = await app.CreateProjectAsync("alice");
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        await using var s = app.Scope(app.UserId("alice"));
        var svc = s.Get<BranchProtectionService>();

        var saved = await svc.SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", RequiredApprovals = 2, MergeQueueMaxBatch = 0, MergeQueueTimeoutMinutes = 0, MergeQueueApprovalTimeoutMinutes = 0 }, app.UserId("alice"));
        Assert.Equal(5, saved.MergeQueueMaxBatch);
        Assert.Equal(60, saved.MergeQueueTimeoutMinutes);
        Assert.Equal(1440, saved.MergeQueueApprovalTimeoutMinutes);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(repo.Id,
            new BranchProtectionRule { Pattern = "release", MergeQueueEnabled = true, MergeQueueMaxBatch = 0 }, app.UserId("alice")));
    }
}
