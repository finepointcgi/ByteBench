using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<TestCase> TestCases => Set<TestCase>();
    public DbSet<TestCaseRevision> TestCaseRevisions => Set<TestCaseRevision>();
    public DbSet<TestPlan> TestPlans => Set<TestPlan>();
    public DbSet<TestPlanCase> TestPlanCases => Set<TestPlanCase>();
    public DbSet<TestRun> TestRuns => Set<TestRun>();
    public DbSet<TestResult> TestResults => Set<TestResult>();
    public DbSet<TestEvidence> TestEvidence => Set<TestEvidence>();
    public DbSet<ReleaseAutoAttachRule> ReleaseAutoAttachRules => Set<ReleaseAutoAttachRule>();

    private static void ConfigureTests(ModelBuilder b)
    {
        b.Entity<TestCase>().HasIndex(c => new { c.ProjectId, c.Number }).IsUnique();
        b.Entity<TestCase>().HasIndex(c => new { c.ProjectId, c.AutomatedName });
        b.Entity<TestCase>().HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TestCaseRevision>().HasIndex(r => new { r.TestCaseId, r.Version }).IsUnique();
        b.Entity<TestCaseRevision>().HasOne(r => r.TestCase).WithMany(c => c.Revisions).HasForeignKey(r => r.TestCaseId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<TestPlan>().HasIndex(p => new { p.ProjectId, p.Name });
        b.Entity<TestPlan>().HasOne<Project>().WithMany().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TestPlanCase>().HasKey(x => new { x.PlanId, x.TestCaseId });
        b.Entity<TestPlanCase>().HasOne(x => x.Plan).WithMany(p => p.Cases).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TestPlanCase>().HasOne(x => x.TestCase).WithMany().HasForeignKey(x => x.TestCaseId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<TestRun>().HasIndex(r => new { r.ProjectId, r.Number }).IsUnique();
        b.Entity<TestRun>().HasIndex(r => new { r.ProjectId, r.CommitSha });
        b.Entity<TestRun>().HasIndex(r => new { r.ProjectId, r.PipelineId });
        b.Entity<TestRun>().HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TestRun>().HasOne(r => r.Plan).WithMany().HasForeignKey(r => r.PlanId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<TestResult>().HasIndex(r => new { r.RunId, r.TestCaseId }).IsUnique();
        b.Entity<TestResult>().HasOne(r => r.Run).WithMany(r => r.Results).HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TestResult>().HasOne(r => r.TestCase).WithMany().HasForeignKey(r => r.TestCaseId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TestEvidence>().HasOne(e => e.Result).WithMany(r => r.Evidence).HasForeignKey(e => e.ResultId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<ReleaseAutoAttachRule>().HasIndex(r => r.RepositoryId).IsUnique();
        b.Entity<ReleaseAutoAttachRule>().HasOne<Repository>().WithMany().HasForeignKey(r => r.RepositoryId).OnDelete(DeleteBehavior.Cascade);
    }
}
