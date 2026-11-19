using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Data;

public partial class AppDbContext(DbContextOptions<AppDbContext> options)
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
    public DbSet<RepositoryAiReviewSettings> RepositoryAiReviewSettings => Set<RepositoryAiReviewSettings>();
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

    // ----- Groups -----
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<GroupRedirect> GroupRedirects => Set<GroupRedirect>();

    private static void ConfigureGroups(ModelBuilder b)
    {
        b.Entity<Group>().HasIndex(g => g.FullPath).IsUnique();
        b.Entity<Group>().HasIndex(g => g.ParentId);
        // GroupService deletes subgroups and projects explicitly (deepest first), so these never cascade by accident.
        b.Entity<Group>().HasOne(g => g.Parent).WithMany(g => g.Children).HasForeignKey(g => g.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Project>().HasOne(p => p.Group).WithMany(g => g.Projects).HasForeignKey(p => p.GroupId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<GroupMember>().HasIndex(m => new { m.GroupId, m.UserId }).IsUnique();
        b.Entity<GroupMember>().HasIndex(m => m.UserId);
        b.Entity<GroupMember>().HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<GroupMember>().HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<GroupRedirect>().HasIndex(r => r.OldPath).IsUnique();
        b.Entity<GroupRedirect>().HasOne(r => r.Group).WithMany().HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
        // Labels and milestones belong to a project or to a group (inherited by its projects). Project ones still cascade with the project.
        b.Entity<Label>().HasOne<Project>().WithMany(p => p.Labels).HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Label>().HasOne<Group>().WithMany().HasForeignKey(l => l.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Label>().HasIndex(l => new { l.GroupId, l.Name }).IsUnique();
        b.Entity<Milestone>().HasOne<Project>().WithMany(p => p.Milestones).HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Milestone>().HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Milestone>().HasIndex(m => m.GroupId);
    }

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

    // ----- Wiki & snippets -----
    public DbSet<WikiPageIndex> WikiPageIndex => Set<WikiPageIndex>();
    public DbSet<Snippet> Snippets => Set<Snippet>();
    public DbSet<SnippetRevision> SnippetRevisions => Set<SnippetRevision>();
    public DbSet<SnippetFile> SnippetFiles => Set<SnippetFile>();
    public DbSet<SnippetComment> SnippetComments => Set<SnippetComment>();

    private static void ConfigureWikiSnippets(ModelBuilder b)
    {
        // Existing projects get the wiki on; the sentinel makes EF send false explicitly (true is left to the default).
        b.Entity<Project>().Property(p => p.WikiEnabled).HasDefaultValue(true).HasSentinel(true);
        b.Entity<WikiPageIndex>().HasIndex(w => new { w.ProjectId, w.Path }).IsUnique();
        b.Entity<WikiPageIndex>().HasOne<Project>().WithMany().HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Snippet>().HasIndex(s => new { s.AuthorId, s.UpdatedAt });
        b.Entity<Snippet>().HasIndex(s => new { s.ProjectId, s.UpdatedAt });
        b.Entity<Snippet>().HasOne(s => s.Author).WithMany().HasForeignKey(s => s.AuthorId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Snippet>().HasOne(s => s.Project).WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SnippetRevision>().HasIndex(r => new { r.SnippetId, r.Number }).IsUnique();
        b.Entity<SnippetRevision>().HasOne<Snippet>().WithMany(s => s.Revisions).HasForeignKey(r => r.SnippetId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SnippetRevision>().HasOne(r => r.Author).WithMany().HasForeignKey(r => r.AuthorId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SnippetFile>().HasIndex(f => new { f.RevisionId, f.Position });
        b.Entity<SnippetFile>().HasOne<SnippetRevision>().WithMany(r => r.Files).HasForeignKey(f => f.RevisionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SnippetComment>().HasIndex(c => new { c.SnippetId, c.CreatedAt });
        b.Entity<SnippetComment>().HasOne<Snippet>().WithMany(s => s.Comments).HasForeignKey(c => c.SnippetId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SnippetComment>().HasOne(c => c.Author).WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Cascade);
    }

    // ----- SSH -----
    public DbSet<SshKey> SshKeys => Set<SshKey>();

    private static void ConfigureSsh(ModelBuilder b)
    {
        b.Entity<SshKey>().HasIndex(k => k.Fingerprint).IsUnique(); // one owner per key, users and deploy keys alike
        b.Entity<SshKey>().HasIndex(k => k.UserId);
        b.Entity<SshKey>().HasIndex(k => k.RepositoryId);
        b.Entity<SshKey>().HasOne(k => k.User).WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SshKey>().HasOne(k => k.Repository).WithMany().HasForeignKey(k => k.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SshKey>().HasOne(k => k.CreatedBy).WithMany().HasForeignKey(k => k.CreatedById).OnDelete(DeleteBehavior.SetNull);
    }

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

    // ----- Explore & profiles -----
    public DbSet<ProjectStar> ProjectStars => Set<ProjectStar>();
    public DbSet<PushEvent> PushEvents => Set<PushEvent>();

    private static void ConfigureExploreProfiles(ModelBuilder b)
    {
        b.Entity<ProjectStar>().HasIndex(s => new { s.UserId, s.ProjectId }).IsUnique();
        b.Entity<ProjectStar>().HasIndex(s => s.ProjectId);
        b.Entity<ProjectStar>().HasOne(s => s.Project).WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectStar>().HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PushEvent>().HasIndex(p => new { p.UserId, p.CreatedAt });
        b.Entity<PushEvent>().HasIndex(p => new { p.ProjectId, p.CreatedAt });
        b.Entity<PushEvent>().HasOne<Project>().WithMany().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PushEvent>().HasOne(p => p.Repository).WithMany().HasForeignKey(p => p.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PushEvent>().HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        // Profile feeds read these by author/actor and date.
        b.Entity<Issue>().HasIndex(i => new { i.ReporterId, i.CreatedAt });
        b.Entity<Activity>().HasIndex(a => new { a.ActorId, a.CreatedAt });
        b.Entity<Comment>().HasIndex(c => new { c.AuthorId, c.CreatedAt });
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
        b.Entity<RepositoryAiReviewSettings>().HasIndex(s => s.RepositoryId).IsUnique();
        b.Entity<RepositoryAiReviewSettings>().HasOne<Repository>().WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequestComment>().HasIndex(c => new { c.PullRequestId, c.AiFingerprint });

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
        ConfigureExploreProfiles(b);
        ConfigureGroups(b);
        ConfigurePrFeatures(b);
        ConfigureSsh(b);
        ConfigureBoards(b);
        ConfigureMirrorTagsLfs(b);
        ConfigureRoadmapDashboards(b);
        ConfigureAutomation(b);
        ConfigureWikiSnippets(b);
        ConfigureForks(b);
        ConfigureSsoLdap(b);
        ConfigureCustomFieldsTime(b);
        ConfigureCiExtras(b);
        ConfigureDeployments(b);
        ConfigureWebEditor(b);
        ConfigurePackages(b);
        ConfigureMergeQueue(b);
        ConfigureSecurity(b);
        ConfigureChatIntegrations(b);
        ConfigureServiceDesk(b);
    }

    // ----- Forks -----
    private static void ConfigureForks(ModelBuilder b)
    {
        // Deleting an upstream leaves its forks working (they are independent clones), just no longer linked.
        b.Entity<Repository>().HasOne(r => r.ForkedFrom).WithMany().HasForeignKey(r => r.ForkedFromId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Repository>().HasIndex(r => r.ForkedFromId);
        // Deleting a fork leaves its PRs open, with an unknown source (refs/pull/{n}/head keeps the commits).
        b.Entity<PullRequest>().HasOne(p => p.SourceRepository).WithMany().HasForeignKey(p => p.SourceRepositoryId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<PullRequest>().HasIndex(p => new { p.SourceRepositoryId, p.State });
        b.Entity<Pipeline>().HasOne(p => p.ApprovedBy).WithMany().HasForeignKey(p => p.ApprovedById).OnDelete(DeleteBehavior.SetNull);
    }

    // ----- SSO & LDAP -----
    public DbSet<ExternalGroupMembership> ExternalGroupMemberships => Set<ExternalGroupMembership>();
    public DbSet<DirectorySyncRun> DirectorySyncRuns => Set<DirectorySyncRun>();

    private static void ConfigureSsoLdap(ModelBuilder b)
    {
        b.Entity<ExternalGroupMembership>().HasIndex(m => new { m.UserId, m.GroupId, m.Source }).IsUnique();
        b.Entity<ExternalGroupMembership>().HasIndex(m => m.GroupId);
        b.Entity<ExternalGroupMembership>().HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ExternalGroupMembership>().HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DirectorySyncRun>().HasIndex(r => new { r.Source, r.StartedAt });
    }

    // ----- Custom fields & time -----
    public DbSet<CustomField> CustomFields => Set<CustomField>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<ProjectComponent> ProjectComponents => Set<ProjectComponent>();
    public DbSet<ProjectVersion> ProjectVersions => Set<ProjectVersion>();
    public DbSet<IssueVersion> IssueVersions => Set<IssueVersion>();
    public DbSet<IssueTemplate> IssueTemplates => Set<IssueTemplate>();

    private static void ConfigureCustomFieldsTime(ModelBuilder b)
    {
        b.Entity<CustomField>().HasIndex(f => new { f.ProjectId, f.Key }).IsUnique();
        b.Entity<CustomField>().HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CustomFieldValue>().HasIndex(v => new { v.IssueId, v.FieldId });
        b.Entity<CustomFieldValue>().HasIndex(v => new { v.FieldId, v.TextValue });
        b.Entity<CustomFieldValue>().HasOne<Issue>().WithMany(i => i.CustomFieldValues).HasForeignKey(v => v.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CustomFieldValue>().HasOne(v => v.Field).WithMany().HasForeignKey(v => v.FieldId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CustomFieldValue>().HasOne(v => v.User).WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<ProjectComponent>().HasIndex(c => new { c.ProjectId, c.Name }).IsUnique();
        b.Entity<ProjectComponent>().HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectComponent>().HasOne(c => c.Lead).WithMany().HasForeignKey(c => c.LeadId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<ProjectComponent>().HasOne(c => c.DefaultAssignee).WithMany().HasForeignKey(c => c.DefaultAssigneeId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Issue>().HasMany(i => i.Components).WithMany(c => c.Issues).UsingEntity("IssueComponents");
        b.Entity<ProjectVersion>().HasIndex(v => new { v.ProjectId, v.Name }).IsUnique();
        b.Entity<ProjectVersion>().HasOne<Project>().WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<IssueVersion>().HasKey(v => new { v.IssueId, v.VersionId, v.Kind });
        b.Entity<IssueVersion>().HasIndex(v => new { v.VersionId, v.Kind });
        b.Entity<IssueVersion>().HasOne<Issue>().WithMany(i => i.VersionLinks).HasForeignKey(v => v.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<IssueVersion>().HasOne(v => v.Version).WithMany().HasForeignKey(v => v.VersionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<IssueTemplate>().HasIndex(t => t.ProjectId);
        b.Entity<IssueTemplate>().HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Project>().Property(p => p.HoursPerDay).HasDefaultValue(8);
    }

    // ----- CI extras: artifacts, caches, schedules, environments & deployments -----
    public DbSet<PipelineArtifact> PipelineArtifacts => Set<PipelineArtifact>();
    public DbSet<CiCacheEntry> CiCacheEntries => Set<CiCacheEntry>();
    public DbSet<PipelineSchedule> PipelineSchedules => Set<PipelineSchedule>();
    public DbSet<CiEnvironment> CiEnvironments => Set<CiEnvironment>();
    public DbSet<Deployment> Deployments => Set<Deployment>();

    private static void ConfigureCiExtras(ModelBuilder b)
    {
        b.Entity<PipelineJob>().HasOne(j => j.PlayedBy).WithMany().HasForeignKey(j => j.PlayedById).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Pipeline>().HasOne<PipelineSchedule>().WithMany().HasForeignKey(p => p.ScheduleId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<PipelineArtifact>().HasIndex(a => a.JobId).IsUnique();
        b.Entity<PipelineArtifact>().HasIndex(a => a.PipelineId);
        b.Entity<PipelineArtifact>().HasIndex(a => a.ExpiresAt);
        b.Entity<PipelineArtifact>().HasIndex(a => a.StoredName).IsUnique();
        b.Entity<PipelineArtifact>().HasOne(a => a.Job).WithMany().HasForeignKey(a => a.JobId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PipelineArtifact>().HasOne<Repository>().WithMany().HasForeignKey(a => a.RepositoryId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<CiCacheEntry>().HasIndex(c => new { c.RepositoryId, c.IsProtected, c.Key }).IsUnique();
        b.Entity<CiCacheEntry>().HasIndex(c => c.StoredName).IsUnique();
        b.Entity<CiCacheEntry>().HasOne<Repository>().WithMany().HasForeignKey(c => c.RepositoryId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<PipelineSchedule>().HasIndex(s => new { s.Active, s.NextRunAt });
        b.Entity<PipelineSchedule>().HasIndex(s => s.RepositoryId);
        b.Entity<PipelineSchedule>().HasOne(s => s.Repository).WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PipelineSchedule>().HasOne(s => s.Owner).WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<CiEnvironment>().HasIndex(e => new { e.RepositoryId, e.Name }).IsUnique();
        b.Entity<CiEnvironment>().HasOne<Repository>().WithMany().HasForeignKey(e => e.RepositoryId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Deployment>().HasIndex(d => new { d.EnvironmentId, d.Id });
        b.Entity<Deployment>().HasIndex(d => d.JobId);
        b.Entity<Deployment>().HasOne(d => d.Environment).WithMany().HasForeignKey(d => d.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Deployment>().HasOne(d => d.Job).WithMany().HasForeignKey(d => d.JobId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Deployment>().HasOne<Pipeline>().WithMany().HasForeignKey(d => d.PipelineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Deployment>().HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.SetNull);
    }

    // ----- PR features: drafts, comment threads, suggestions, merge strategies -----
    private static void ConfigurePrFeatures(ModelBuilder b)
    {
        b.Entity<PullRequestComment>().HasOne<PullRequestComment>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequestComment>().HasIndex(c => c.ParentId);
        b.Entity<PullRequestComment>().HasOne(c => c.ResolvedBy).WithMany().HasForeignKey(c => c.ResolvedById).OnDelete(DeleteBehavior.SetNull);
    }

    // ----- Boards -----
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardColumn> BoardColumns => Set<BoardColumn>();
    public DbSet<BoardQuickFilter> BoardQuickFilters => Set<BoardQuickFilter>();

    private static void ConfigureBoards(ModelBuilder b)
    {
        b.Entity<Board>().HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        // One default board per project, enforced by the database too (so a lazy-creation race can't make two).
        b.Entity<Board>().HasIndex(x => x.ProjectId, "IX_Boards_OneDefaultPerProject").IsUnique().HasFilter("\"IsDefault\" = 1");
        b.Entity<BoardColumn>().HasOne<Board>().WithMany(x => x.Columns).HasForeignKey(c => c.BoardId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BoardQuickFilter>().HasOne<Board>().WithMany(x => x.QuickFilters).HasForeignKey(q => q.BoardId).OnDelete(DeleteBehavior.Cascade);
    }

    // ----- Mirroring, tags & LFS -----
    public DbSet<ProtectedTagRule> ProtectedTagRules => Set<ProtectedTagRule>();
    public DbSet<RepositoryMirror> RepositoryMirrors => Set<RepositoryMirror>();
    public DbSet<LfsObject> LfsObjects => Set<LfsObject>();

    private static void ConfigureMirrorTagsLfs(ModelBuilder b)
    {
        b.Entity<ProtectedTagRule>().HasIndex(r => new { r.RepositoryId, r.Pattern }).IsUnique();
        b.Entity<ProtectedTagRule>().HasOne(r => r.Repository).WithMany().HasForeignKey(r => r.RepositoryId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<RepositoryMirror>().HasIndex(m => m.RepositoryId);
        // At most one pull mirror per repository (Direction 0 = Pull).
        b.Entity<RepositoryMirror>().HasIndex(m => m.RepositoryId, "IX_RepositoryMirrors_OnePullPerRepository").IsUnique().HasFilter("\"Direction\" = 0");
        b.Entity<RepositoryMirror>().HasIndex(m => new { m.Direction, m.Enabled, m.NextSyncAt });
        b.Entity<RepositoryMirror>().HasOne(m => m.Repository).WithMany().HasForeignKey(m => m.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RepositoryMirror>().HasOne(m => m.CreatedBy).WithMany().HasForeignKey(m => m.CreatedById).OnDelete(DeleteBehavior.SetNull);

        b.Entity<LfsObject>().HasIndex(o => new { o.RepositoryId, o.Oid }).IsUnique();
        b.Entity<LfsObject>().HasIndex(o => o.Oid);
        b.Entity<LfsObject>().HasOne(o => o.Repository).WithMany().HasForeignKey(o => o.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LfsObject>().HasOne(o => o.UploadedBy).WithMany().HasForeignKey(o => o.UploadedById).OnDelete(DeleteBehavior.SetNull);
    }

    // ----- Roadmap & dashboards -----
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();
    public DbSet<DashboardGadget> DashboardGadgets => Set<DashboardGadget>();
    public DbSet<DashboardHome> DashboardHomes => Set<DashboardHome>();

    private static void ConfigureRoadmapDashboards(ModelBuilder b)
    {
        b.Entity<Dashboard>().HasIndex(d => d.OwnerId);
        b.Entity<Dashboard>().HasIndex(d => d.SharedProjectId);
        b.Entity<Dashboard>().HasIndex(d => d.SharedGroupId);
        b.Entity<Dashboard>().HasOne(d => d.Owner).WithMany().HasForeignKey(d => d.OwnerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Dashboard>().HasOne<Project>().WithMany().HasForeignKey(d => d.SharedProjectId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Dashboard>().HasOne<Group>().WithMany().HasForeignKey(d => d.SharedGroupId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<DashboardGadget>().HasIndex(g => new { g.DashboardId, g.Order });
        b.Entity<DashboardGadget>().HasOne<Dashboard>().WithMany(d => d.Gadgets).HasForeignKey(g => g.DashboardId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DashboardHome>().HasKey(h => h.UserId);
        b.Entity<DashboardHome>().HasIndex(h => h.DashboardId);
        b.Entity<DashboardHome>().HasOne<User>().WithMany().HasForeignKey(h => h.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DashboardHome>().HasOne<Dashboard>().WithMany().HasForeignKey(h => h.DashboardId).OnDelete(DeleteBehavior.Cascade);
    }

    // ----- Automation -----
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();

    private static void ConfigureAutomation(ModelBuilder b)
    {
        b.Entity<AutomationRule>().HasIndex(r => r.ProjectId);
        b.Entity<AutomationRule>().HasIndex(r => r.GroupId);
        b.Entity<AutomationRule>().HasOne(r => r.Project).WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AutomationRule>().HasOne(r => r.Group).WithMany().HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AutomationRule>().HasOne(r => r.Author).WithMany().HasForeignKey(r => r.AuthorId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<AutomationRule>().ToTable(t => t.HasCheckConstraint("CK_AutomationRules_Scope", "(\"ProjectId\" IS NULL) <> (\"GroupId\" IS NULL)"));
        b.Entity<AutomationRun>().HasIndex(r => new { r.RuleId, r.StartedAt });
        b.Entity<AutomationRun>().HasOne(r => r.Rule).WithMany().HasForeignKey(r => r.RuleId).OnDelete(DeleteBehavior.Cascade);
    }

    // ----- Web editor -----
    public DbSet<EditorWorkspace> EditorWorkspaces => Set<EditorWorkspace>();
    public DbSet<EditorWorkspaceChange> EditorWorkspaceChanges => Set<EditorWorkspaceChange>();
    public DbSet<ProjectEditorSettings> ProjectEditorSettings => Set<ProjectEditorSettings>();

    private static void ConfigureWebEditor(ModelBuilder b)
    {
        b.Entity<EditorWorkspace>().HasIndex(w => new { w.UserId, w.RepositoryId, w.Branch }).IsUnique();
        b.Entity<EditorWorkspace>().HasIndex(w => w.RepositoryId);
        b.Entity<EditorWorkspace>().HasOne(w => w.User).WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EditorWorkspace>().HasOne(w => w.Repository).WithMany().HasForeignKey(w => w.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EditorWorkspaceChange>().HasIndex(c => new { c.WorkspaceId, c.Path }).IsUnique();
        b.Entity<EditorWorkspaceChange>().HasOne<EditorWorkspace>().WithMany(w => w.Changes).HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectEditorSettings>().HasKey(s => s.ProjectId);
        b.Entity<ProjectEditorSettings>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
    // ----- Package & container registry -----
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();
    public DbSet<PackageFile> PackageFiles => Set<PackageFile>();
    public DbSet<ContainerBlob> ContainerBlobs => Set<ContainerBlob>();
    public DbSet<PackageDistTag> PackageDistTags => Set<PackageDistTag>();
    public DbSet<ProjectPackageSettings> ProjectPackageSettings => Set<ProjectPackageSettings>();

    private static void ConfigurePackages(ModelBuilder b)
    {
        b.Entity<Package>().HasIndex(p => new { p.ProjectId, p.Type, p.NormalizedName }).IsUnique();
        b.Entity<Package>().HasOne(p => p.Project).WithMany().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PackageVersion>().HasIndex(v => new { v.PackageId, v.NormalizedVersion }).IsUnique();
        b.Entity<PackageVersion>().HasOne(v => v.Package).WithMany(p => p.Versions).HasForeignKey(v => v.PackageId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PackageVersion>().HasOne(v => v.PublishedBy).WithMany().HasForeignKey(v => v.PublishedById).OnDelete(DeleteBehavior.SetNull);
        b.Entity<PackageFile>().HasIndex(f => f.PackageVersionId);
        b.Entity<PackageFile>().HasIndex(f => f.Sha256);
        b.Entity<PackageFile>().HasOne<PackageVersion>().WithMany(v => v.Files).HasForeignKey(f => f.PackageVersionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ContainerBlob>().HasIndex(x => new { x.PackageId, x.Sha256 }).IsUnique();
        b.Entity<ContainerBlob>().HasIndex(x => x.Sha256);
        b.Entity<ContainerBlob>().HasOne(x => x.Package).WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PackageDistTag>().HasIndex(t => new { t.PackageId, t.Tag }).IsUnique();
        b.Entity<PackageDistTag>().HasOne<Package>().WithMany().HasForeignKey(t => t.PackageId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectPackageSettings>().HasKey(s => s.ProjectId);
        b.Entity<ProjectPackageSettings>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }


    // ----- Merge queue -----
    public DbSet<MergeQueueEntry> MergeQueueEntries => Set<MergeQueueEntry>();

    private static void ConfigureMergeQueue(ModelBuilder b)
    {
        b.Entity<MergeQueueEntry>().HasIndex(e => new { e.RepositoryId, e.TargetBranch, e.State, e.Position });
        b.Entity<MergeQueueEntry>().HasIndex(e => new { e.PullRequestId, e.State });
        b.Entity<MergeQueueEntry>().HasOne(e => e.Repository).WithMany().HasForeignKey(e => e.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MergeQueueEntry>().HasOne(e => e.PullRequest).WithMany().HasForeignKey(e => e.PullRequestId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MergeQueueEntry>().HasOne(e => e.EnqueuedBy).WithMany().HasForeignKey(e => e.EnqueuedById).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MergeQueueEntry>().HasOne(e => e.Pipeline).WithMany().HasForeignKey(e => e.PipelineId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<MergeQueueEntry>().HasOne<User>().WithMany().HasForeignKey(e => e.RemovedById).OnDelete(DeleteBehavior.SetNull);
    }


    // ----- Security scanning (Night Owl) -----
    public DbSet<SecurityScan> SecurityScans => Set<SecurityScan>();
    public DbSet<SecurityFinding> SecurityFindings => Set<SecurityFinding>();
    public DbSet<SecurityTriage> SecurityTriages => Set<SecurityTriage>();
    public DbSet<RepositorySecuritySettings> RepositorySecuritySettings => Set<RepositorySecuritySettings>();

    private static void ConfigureSecurity(ModelBuilder b)
    {
        b.Entity<SecurityScan>().HasIndex(s => new { s.RepositoryId, s.Ref, s.PipelineId });
        b.Entity<SecurityScan>().HasIndex(s => new { s.RepositoryId, s.CommitSha });
        b.Entity<SecurityScan>().HasIndex(s => s.PipelineId);
        b.Entity<SecurityScan>().HasOne<Repository>().WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SecurityScan>().HasOne<Pipeline>().WithMany().HasForeignKey(s => s.PipelineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SecurityFinding>().HasIndex(f => new { f.ScanId, f.Fingerprint }).IsUnique();
        b.Entity<SecurityFinding>().HasIndex(f => new { f.RepositoryId, f.Fingerprint });
        b.Entity<SecurityFinding>().HasOne(f => f.Scan).WithMany(s => s.Findings).HasForeignKey(f => f.ScanId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SecurityTriage>().HasIndex(t => new { t.RepositoryId, t.Fingerprint }).IsUnique();
        b.Entity<SecurityTriage>().HasOne<Repository>().WithMany().HasForeignKey(t => t.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SecurityTriage>().HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<RepositorySecuritySettings>().HasKey(s => s.RepositoryId);
        b.Entity<RepositorySecuritySettings>().HasOne<Repository>().WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
    }


    // ----- Chat integrations -----
    public DbSet<ChatUserLink> ChatUserLinks => Set<ChatUserLink>();

    private static void ConfigureChatIntegrations(ModelBuilder b)
    {
        b.Entity<ChatUserLink>().HasIndex(l => new { l.Provider, l.TeamId, l.ExternalUserId }).IsUnique().HasFilter("\"RevokedAt\" IS NULL");
        b.Entity<ChatUserLink>().HasIndex(l => l.Nonce).IsUnique();
        b.Entity<ChatUserLink>().HasIndex(l => l.UserId);
        b.Entity<ChatUserLink>().HasOne(l => l.User).WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
    }


    // ----- Service desk -----
    public DbSet<ServiceDeskSettings> ServiceDeskSettings => Set<ServiceDeskSettings>();
    public DbSet<ServiceDeskContact> ServiceDeskContacts => Set<ServiceDeskContact>();
    public DbSet<ServiceDeskTicket> ServiceDeskTickets => Set<ServiceDeskTicket>();
    public DbSet<ServiceDeskMessage> ServiceDeskMessages => Set<ServiceDeskMessage>();

    private static void ConfigureServiceDesk(ModelBuilder b)
    {
        b.Entity<ServiceDeskSettings>().HasKey(s => s.ProjectId);
        b.Entity<ServiceDeskSettings>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ServiceDeskSettings>().HasOne<User>().WithMany().HasForeignKey(s => s.DefaultAssigneeId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<ServiceDeskSettings>().HasIndex(s => s.CustomAddress).IsUnique();
        b.Entity<ServiceDeskContact>().HasIndex(c => c.Email).IsUnique();
        b.Entity<ServiceDeskTicket>().HasIndex(t => t.IssueId).IsUnique();
        b.Entity<ServiceDeskTicket>().HasIndex(t => t.ReplyToken).IsUnique();
        b.Entity<ServiceDeskTicket>().HasIndex(t => t.TrackingToken).IsUnique();
        b.Entity<ServiceDeskTicket>().HasIndex(t => new { t.ProjectId, t.LastRequesterActivityAt });
        b.Entity<ServiceDeskTicket>().HasOne(t => t.Issue).WithMany().HasForeignKey(t => t.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ServiceDeskTicket>().HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ServiceDeskTicket>().HasOne(t => t.Contact).WithMany().HasForeignKey(t => t.ContactId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ServiceDeskMessage>().HasIndex(m => m.MessageId);
        b.Entity<ServiceDeskMessage>().HasIndex(m => m.TicketId);
        b.Entity<ServiceDeskMessage>().HasOne(m => m.Ticket).WithMany().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Comment>().HasOne<ServiceDeskContact>().WithMany().HasForeignKey(c => c.ServiceDeskContactId).OnDelete(DeleteBehavior.SetNull);
    }
}
