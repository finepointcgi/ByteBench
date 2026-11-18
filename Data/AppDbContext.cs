using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<User, IdentityRole<int>, int>(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<AiUsage> AiUsages => Set<AiUsage>();
    public DbSet<PullRequest> PullRequests => Set<PullRequest>();
    public DbSet<PullRequestComment> PullRequestComments => Set<PullRequestComment>();
    public DbSet<PullRequestReview> PullRequestReviews => Set<PullRequestReview>();
    public DbSet<PullRequestActivity> PullRequestActivities => Set<PullRequestActivity>();
    public DbSet<AiReview> AiReviews => Set<AiReview>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b); // Identity tables (AspNetUsers, AspNetRoles, …)
        b.Entity<Project>().HasIndex(p => p.Key).IsUnique();

        b.Entity<Issue>().HasIndex(i => new { i.ProjectId, i.Number }).IsUnique();
        b.Entity<Issue>().HasOne(i => i.Assignee).WithMany().HasForeignKey(i => i.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Issue>().HasOne(i => i.Reporter).WithMany().HasForeignKey(i => i.ReporterId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Issue>().HasOne(i => i.Milestone).WithMany(m => m.Issues).HasForeignKey(i => i.MilestoneId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Issue>().HasMany(i => i.Labels).WithMany(l => l.Issues);
        b.Entity<Issue>().HasMany(i => i.Comments).WithOne().HasForeignKey(c => c.IssueId);
        b.Entity<Issue>().HasMany(i => i.Activities).WithOne(a => a.Issue).HasForeignKey(a => a.IssueId);

        b.Entity<Label>().HasIndex(l => new { l.ProjectId, l.Name }).IsUnique();
        b.Entity<Repository>().HasIndex(r => new { r.ProjectId, r.Slug }).IsUnique();

        // ----- Pull requests -----
        b.Entity<PullRequest>().HasIndex(p => new { p.RepositoryId, p.Number }).IsUnique();
        b.Entity<PullRequest>().HasOne(p => p.Repository).WithMany().HasForeignKey(p => p.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequest>().HasOne(p => p.Author).WithMany().HasForeignKey(p => p.AuthorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PullRequest>().HasOne(p => p.MergedBy).WithMany().HasForeignKey(p => p.MergedById).OnDelete(DeleteBehavior.SetNull);
        b.Entity<PullRequest>().HasMany(p => p.Comments).WithOne().HasForeignKey(c => c.PullRequestId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequest>().HasMany(p => p.Reviews).WithOne().HasForeignKey(r => r.PullRequestId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequest>().HasMany(p => p.Activities).WithOne().HasForeignKey(a => a.PullRequestId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequestComment>().HasOne(c => c.Author).WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PullRequestReview>().HasIndex(r => new { r.PullRequestId, r.UserId }).IsUnique();
        b.Entity<PullRequestReview>().HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequestActivity>().HasOne(a => a.Actor).WithMany().HasForeignKey(a => a.ActorId).OnDelete(DeleteBehavior.Restrict);

        // ----- AI code review -----
        b.Entity<AiReview>().HasIndex(r => new { r.PullRequestId, r.HeadSha });
        b.Entity<AiReview>().HasOne<PullRequest>().WithMany().HasForeignKey(r => r.PullRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
