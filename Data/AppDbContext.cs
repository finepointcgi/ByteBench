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
    // ----- Sprints -----
    public DbSet<Sprint> Sprints => Set<Sprint>();
    // ----- Workflows & audit -----
    public DbSet<WorkflowStatus> WorkflowStatuses => Set<WorkflowStatus>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    // ----- Email & preferences -----
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<UserNotificationSettings> UserNotificationSettings => Set<UserNotificationSettings>();

    private static void ConfigureEmailAndPreferences(ModelBuilder b)
    {
        b.Entity<NotificationPreference>().HasIndex(p => new { p.UserId, p.Group }).IsUnique();
        b.Entity<NotificationPreference>().HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserNotificationSettings>().HasOne<User>().WithOne().HasForeignKey<UserNotificationSettings>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        // The email worker polls for pending rows.
        b.Entity<Notification>().HasIndex(n => n.EmailPending).HasFilter("\"EmailPending\" = 1");
    }

    // ----- Admin, 2FA & CI secrets -----
    public DbSet<CiVariable> CiVariables => Set<CiVariable>();

    private static void ConfigureAdminSecurity(ModelBuilder b)
    {
        b.Entity<CiVariable>().HasIndex(v => new { v.ProjectId, v.RepositoryId, v.Name });
        b.Entity<CiVariable>().HasOne<Project>().WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CiVariable>().HasOne<Repository>().WithMany().HasForeignKey(v => v.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CiVariable>().HasOne(v => v.UpdatedBy).WithMany().HasForeignKey(v => v.UpdatedById).OnDelete(DeleteBehavior.SetNull);
    }

    // ----- Releases -----
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<ReleaseAsset> ReleaseAssets => Set<ReleaseAsset>();

    private static void ConfigureReleases(ModelBuilder b)
    {
        b.Entity<Release>().HasIndex(r => new { r.RepositoryId, r.TagName }).IsUnique();
        b.Entity<Release>().HasIndex(r => new { r.RepositoryId, r.CreatedAt });
        b.Entity<Release>().HasOne(r => r.Repository).WithMany().HasForeignKey(r => r.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Release>().HasOne(r => r.Author).WithMany().HasForeignKey(r => r.AuthorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ReleaseAsset>().HasIndex(a => a.StoredName).IsUnique();
        b.Entity<ReleaseAsset>().HasOne(a => a.Release).WithMany(r => r.Assets).HasForeignKey(a => a.ReleaseId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ReleaseAsset>().HasOne(a => a.Uploader).WithMany().HasForeignKey(a => a.UploaderId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWorkflowsAndAudit(ModelBuilder b)
    {
        b.Entity<WorkflowStatus>().HasIndex(s => new { s.ProjectId, s.Order });
        b.Entity<WorkflowStatus>().HasOne<Project>().WithMany(p => p.WorkflowStatuses).HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        // SetNull rather than Restrict so deleting a project (which cascades to issues and statuses) never trips over the order;
        // WorkflowService refuses to delete a status that still has issues.
        b.Entity<Issue>().HasOne(i => i.WorkflowStatus).WithMany().HasForeignKey(i => i.WorkflowStatusId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<WorkflowTransition>().HasIndex(t => new { t.FromStatusId, t.ToStatusId }).IsUnique();
        b.Entity<WorkflowTransition>().HasIndex(t => t.ProjectId);
        b.Entity<WorkflowTransition>().HasOne<WorkflowStatus>().WithMany().HasForeignKey(t => t.FromStatusId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkflowTransition>().HasOne<WorkflowStatus>().WithMany().HasForeignKey(t => t.ToStatusId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AuditEvent>().HasIndex(e => e.CreatedAt);
        b.Entity<AuditEvent>().HasIndex(e => new { e.ProjectId, e.CreatedAt });
        b.Entity<AuditEvent>().HasIndex(e => new { e.ActorId, e.CreatedAt });
        b.Entity<AuditEvent>().HasIndex(e => e.Action);
    }

    /// <summary>Audit events are append-only: refuse tracked updates and deletes (retention cleanup uses ExecuteDelete).</summary>
    private void GuardAuditEvents()
    {
        if (ChangeTracker.Entries<AuditEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit events are append-only.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditEvents();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAuditEvents();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

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

        // ----- Sprints -----
        b.Entity<Sprint>().HasIndex(s => new { s.ProjectId, s.State });
        // At most one active sprint per project, enforced by the database too (State 1 = Active).
        b.Entity<Sprint>().HasIndex(s => s.ProjectId).IsUnique().HasFilter("\"State\" = 1").HasDatabaseName("IX_Sprints_OneActivePerProject");
        b.Entity<Sprint>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Issue>().HasOne(i => i.Sprint).WithMany().HasForeignKey(i => i.SprintId).OnDelete(DeleteBehavior.SetNull);
        ConfigureWorkflowsAndAudit(b);
        ConfigureEmailAndPreferences(b);
        ConfigureAdminSecurity(b);
        ConfigureReleases(b);
    }
}
