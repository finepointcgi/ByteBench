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
    }
}
