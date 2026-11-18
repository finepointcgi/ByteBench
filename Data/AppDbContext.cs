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
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Watch> Watches => Set<Watch>();
    public DbSet<AiReview> AiReviews => Set<AiReview>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<RepositoryRedirect> RepositoryRedirects => Set<RepositoryRedirect>();

    // ----- Branch protection & reviewers -----
    public DbSet<BranchProtectionRule> BranchProtectionRules => Set<BranchProtectionRule>();
    public DbSet<PullRequestReviewer> PullRequestReviewers => Set<PullRequestReviewer>();
    // ----- Pipelines -----
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineJob> PipelineJobs => Set<PipelineJob>();
    public DbSet<PipelineStep> PipelineSteps => Set<PipelineStep>();
    public DbSet<PipelineStepLog> PipelineStepLogs => Set<PipelineStepLog>();

    // ----- Webhooks & API -----
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    // ----- Linked work -----
    public DbSet<IssueLink> IssueLinks => Set<IssueLink>();
    // ----- Attachments & filters -----
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();
    public DbSet<SavedFilterStar> SavedFilterStars => Set<SavedFilterStar>();

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

        // ----- Notifications & watches -----
        b.Entity<Notification>().HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt });
        b.Entity<Notification>().HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Notification>().HasOne(n => n.Actor).WithMany().HasForeignKey(n => n.ActorId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Notification>().HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Watch>().ToTable(t => t.HasCheckConstraint("CK_Watch_OneTarget", "(IssueId IS NULL) <> (PullRequestId IS NULL)"));
        b.Entity<Watch>().HasIndex(w => new { w.UserId, w.IssueId }).IsUnique();
        b.Entity<Watch>().HasIndex(w => new { w.UserId, w.PullRequestId }).IsUnique();
        b.Entity<Watch>().HasOne<User>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Watch>().HasOne<Issue>().WithMany().HasForeignKey(w => w.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Watch>().HasOne<PullRequest>().WithMany().HasForeignKey(w => w.PullRequestId).OnDelete(DeleteBehavior.Cascade);

        // ----- AI code review -----
        b.Entity<AiReview>().HasIndex(r => new { r.PullRequestId, r.HeadSha });
        b.Entity<AiReview>().HasOne<PullRequest>().WithMany().HasForeignKey(r => r.PullRequestId).OnDelete(DeleteBehavior.Cascade);

        // ----- Membership & access tokens -----
        b.Entity<ProjectMember>().HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique();
        b.Entity<ProjectMember>().HasOne(m => m.Project).WithMany(p => p.Members).HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectMember>().HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PersonalAccessToken>().HasIndex(t => t.UserId);
        b.Entity<PersonalAccessToken>().HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);

        // ----- Branch protection & reviewers -----
        b.Entity<BranchProtectionRule>().HasIndex(r => new { r.RepositoryId, r.Pattern }).IsUnique();
        b.Entity<BranchProtectionRule>().HasOne(r => r.Repository).WithMany().HasForeignKey(r => r.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequestReviewer>().HasIndex(r => new { r.PullRequestId, r.UserId }).IsUnique();
        b.Entity<PullRequestReviewer>().HasIndex(r => r.UserId);
        b.Entity<PullRequestReviewer>().HasOne<PullRequest>().WithMany().HasForeignKey(r => r.PullRequestId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequestReviewer>().HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequestReviewer>().HasOne<User>().WithMany().HasForeignKey(r => r.AddedById).OnDelete(DeleteBehavior.SetNull);
        // ----- Pipelines -----
        b.Entity<Pipeline>().HasIndex(p => new { p.RepositoryId, p.Id });
        b.Entity<Pipeline>().HasIndex(p => new { p.RepositoryId, p.CommitSha });
        b.Entity<Pipeline>().HasIndex(p => p.Status);
        b.Entity<Pipeline>().HasOne(p => p.Repository).WithMany().HasForeignKey(p => p.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Pipeline>().HasOne(p => p.TriggeredBy).WithMany().HasForeignKey(p => p.TriggeredById).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Pipeline>().HasOne<PullRequest>().WithMany().HasForeignKey(p => p.PullRequestId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<PipelineJob>().HasOne(j => j.Pipeline).WithMany(p => p.Jobs).HasForeignKey(j => j.PipelineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PipelineStep>().HasOne(s => s.Job).WithMany(j => j.Steps).HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PipelineStepLog>().HasKey(l => l.StepId);
        b.Entity<PipelineStep>().HasOne(s => s.Log).WithOne().HasForeignKey<PipelineStepLog>(l => l.StepId).OnDelete(DeleteBehavior.Cascade);
        // ----- Code tools -----
        b.Entity<RepositoryRedirect>().HasIndex(r => new { r.ProjectId, r.OldSlug }).IsUnique();
        b.Entity<RepositoryRedirect>().HasOne(r => r.Repository).WithMany().HasForeignKey(r => r.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        // ----- Webhooks & API -----
        b.Entity<PersonalAccessToken>().HasIndex(t => t.TokenHash); // Bearer tokens are looked up by hash
        b.Entity<Webhook>().HasIndex(w => w.ProjectId);
        b.Entity<Webhook>().HasOne<Project>().WithMany().HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Webhook>().HasOne<User>().WithMany().HasForeignKey(w => w.CreatedById).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WebhookDelivery>().HasIndex(d => new { d.WebhookId, d.CreatedAt });
        b.Entity<WebhookDelivery>().HasOne<Webhook>().WithMany().HasForeignKey(d => d.WebhookId).OnDelete(DeleteBehavior.Cascade);
        // ----- Linked work -----
        // Sub-tasks are deleted by IssueService with their parent; anything else left pointing at a deleted parent is unlinked.
        b.Entity<Issue>().HasOne(i => i.Parent).WithMany(p => p.Children).HasForeignKey(i => i.ParentId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<IssueLink>().HasIndex(l => new { l.SourceIssueId, l.TargetIssueId, l.Type }).IsUnique();
        b.Entity<IssueLink>().HasIndex(l => l.TargetIssueId);
        b.Entity<IssueLink>().HasOne(l => l.SourceIssue).WithMany().HasForeignKey(l => l.SourceIssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<IssueLink>().HasOne(l => l.TargetIssue).WithMany().HasForeignKey(l => l.TargetIssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<IssueLink>().HasOne<User>().WithMany().HasForeignKey(l => l.CreatedById).OnDelete(DeleteBehavior.SetNull);
        // ----- Attachments & filters -----
        b.Entity<Attachment>().HasIndex(a => new { a.IssueId, a.CreatedAt });
        b.Entity<Attachment>().HasIndex(a => a.StoredName).IsUnique();
        b.Entity<Attachment>().HasOne(a => a.Issue).WithMany().HasForeignKey(a => a.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Attachment>().HasOne(a => a.Uploader).WithMany().HasForeignKey(a => a.UploaderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SavedFilter>().HasIndex(f => new { f.ProjectId, f.OwnerId });
        b.Entity<SavedFilter>().HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SavedFilter>().HasOne(f => f.Owner).WithMany().HasForeignKey(f => f.OwnerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SavedFilterStar>().HasIndex(s => new { s.UserId, s.SavedFilterId }).IsUnique();
        b.Entity<SavedFilterStar>().HasOne<SavedFilter>().WithMany().HasForeignKey(s => s.SavedFilterId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SavedFilterStar>().HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
