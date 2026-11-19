using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>One user's resolved preferences: every group has a channel (stored or default).</summary>
public sealed record UserNotificationPreferences(bool EmailEnabled, IReadOnlyDictionary<NotificationGroup, NotificationChannel> Channels,
    bool SlackDmEnabled = false)
{
    public NotificationChannel ChannelFor(NotificationKind kind) => Channels[NotificationPreferences.GroupOf(kind)];

    public static readonly UserNotificationPreferences Defaults = new(true,
        NotificationPreferences.Groups.ToDictionary(g => g.Group, g => g.Default));
}

/// <summary>
/// Per-user notification preferences (page: <c>/account/notifications</c>). Every <see cref="NotificationKind"/> maps to one
/// <see cref="NotificationGroup"/>, and each group is In-app + email, In-app only or Off. Applied by
/// <see cref="NotificationService"/> at its single fan-out point (<c>DeliverAsync</c>), so every kind respects them.
/// </summary>
public class NotificationPreferences(IDbContextFactory<AppDbContext> dbFactory)
{
    public sealed record GroupInfo(NotificationGroup Group, string Title, string Description, NotificationChannel Default);

    /// <summary>The groups in page order, with their defaults: in-app for everything, email for assigned, mentioned and review requested.</summary>
    public static readonly IReadOnlyList<GroupInfo> Groups =
    [
        new(NotificationGroup.Assigned, "Assigned to me", "An issue is assigned to you.", NotificationChannel.InAppAndEmail),
        new(NotificationGroup.Mentioned, "Mentions", "Someone @mentions you in an issue, pull request or comment.", NotificationChannel.InAppAndEmail),
        new(NotificationGroup.ReviewRequested, "Review requests", "You're added as a reviewer on a pull request.", NotificationChannel.InAppAndEmail),
        new(NotificationGroup.Reviews, "Pull request reviews", "Approvals and change requests on pull requests you author or watch.", NotificationChannel.InApp),
        new(NotificationGroup.WatchedComments, "Comments on watched items", "New comments on issues and pull requests you watch.", NotificationChannel.InApp),
        new(NotificationGroup.WatchedChanges, "Changes to watched items", "Status changes, close and reopen on watched issues; merge, decline and reopen on watched pull requests.", NotificationChannel.InApp),
        new(NotificationGroup.IssueLinks, "Issue links & hierarchy", "Links, parents and sub-tasks of issues you watch change.", NotificationChannel.InApp),
        new(NotificationGroup.Pipelines, "Pipelines", "A pipeline you triggered fails.", NotificationChannel.InApp),
    ];

    public static NotificationGroup GroupOf(NotificationKind kind) => kind switch
    {
        NotificationKind.Assigned => NotificationGroup.Assigned,
        NotificationKind.Mentioned => NotificationGroup.Mentioned,
        NotificationKind.ReviewRequested => NotificationGroup.ReviewRequested,
        NotificationKind.PullRequestReviewed => NotificationGroup.Reviews,
        NotificationKind.IssueCommented or NotificationKind.PullRequestCommented => NotificationGroup.WatchedComments,
        NotificationKind.IssueStatusChanged or NotificationKind.IssueClosed or NotificationKind.IssueReopened
            or NotificationKind.PullRequestMerged or NotificationKind.PullRequestDeclined or NotificationKind.PullRequestReopened
            => NotificationGroup.WatchedChanges,
        NotificationKind.IssueLinked or NotificationKind.IssueHierarchyChanged => NotificationGroup.IssueLinks,
        NotificationKind.PipelineFailed or NotificationKind.SecurityFindings => NotificationGroup.Pipelines,
        _ => NotificationGroup.WatchedChanges, // a new kind lands somewhere sensible until it gets its own group
    };

    public async Task<UserNotificationPreferences> GetAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await LoadAsync(db, [userId]))[userId];
    }

    /// <summary>Saves the master email switch and every group's channel (rows equal to the default are removed).</summary>
    public async Task SaveAsync(int userId, bool emailEnabled, IReadOnlyDictionary<NotificationGroup, NotificationChannel> channels)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await SetEmailEnabledAsync(db, userId, emailEnabled);
        var existing = await db.NotificationPreferences.Where(p => p.UserId == userId).ToDictionaryAsync(p => p.Group);
        foreach (var g in Groups)
        {
            if (!channels.TryGetValue(g.Group, out var channel) || !Enum.IsDefined(channel)) continue;
            existing.TryGetValue(g.Group, out var row);
            if (channel == g.Default) { if (row != null) db.NotificationPreferences.Remove(row); }
            else if (row != null) row.Channel = channel;
            else db.NotificationPreferences.Add(new NotificationPreference { UserId = userId, Group = g.Group, Channel = channel });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Also deliver notifications (every group not turned Off) as Slack DMs. Takes effect only when the server has a Slack
    /// bot token and the user linked their Slack account (<c>/gitbench link</c>).
    /// </summary>
    public async Task SetSlackDmEnabledAsync(int userId, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.UserNotificationSettings.FindAsync(userId);
        if (s == null) db.UserNotificationSettings.Add(new UserNotificationSettings { UserId = userId, SlackDmEnabled = enabled });
        else s.SlackDmEnabled = enabled;
        if (!enabled)
            await db.Notifications.Where(n => n.UserId == userId && n.ChatPending)
                .ExecuteUpdateAsync(x => x.SetProperty(n => n.ChatPending, false));
        await db.SaveChangesAsync();
    }

    /// <summary>Turns notification emails on or off for <paramref name="userId"/> (the one-click unsubscribe link uses this).</summary>
    public async Task SetEmailEnabledAsync(int userId, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await SetEmailEnabledAsync(db, userId, enabled);
        await db.SaveChangesAsync();
    }

    private static async Task SetEmailEnabledAsync(AppDbContext db, int userId, bool enabled)
    {
        var s = await db.UserNotificationSettings.FindAsync(userId);
        if (s == null) db.UserNotificationSettings.Add(new UserNotificationSettings { UserId = userId, EmailEnabled = enabled });
        else s.EmailEnabled = enabled;
        // Turning email off also drops anything waiting for the next digest.
        if (!enabled)
            await db.Notifications.Where(n => n.UserId == userId && n.EmailPending)
                .ExecuteUpdateAsync(x => x.SetProperty(n => n.EmailPending, false));
    }

    /// <summary>Resolved preferences for each of <paramref name="userIds"/> (defaults for users with no rows).</summary>
    public static async Task<Dictionary<int, UserNotificationPreferences>> LoadAsync(AppDbContext db, IReadOnlyCollection<int> userIds)
    {
        var rows = (await db.NotificationPreferences.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToListAsync())
            .ToLookup(p => p.UserId);
        var emailOff = (await db.UserNotificationSettings.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId) && !s.EmailEnabled).Select(s => s.UserId).ToListAsync()).ToHashSet();

        var slackOn = (await db.UserNotificationSettings.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId) && s.SlackDmEnabled).Select(s => s.UserId).ToListAsync()).ToHashSet();

        var result = new Dictionary<int, UserNotificationPreferences>();
        foreach (var id in userIds.Distinct())
        {
            var mine = rows[id].ToDictionary(p => p.Group, p => p.Channel);
            if (mine.Count == 0 && !emailOff.Contains(id) && !slackOn.Contains(id)) { result[id] = UserNotificationPreferences.Defaults; continue; }
            result[id] = new(!emailOff.Contains(id),
                Groups.ToDictionary(g => g.Group, g => mine.TryGetValue(g.Group, out var c) ? c : g.Default), slackOn.Contains(id));
        }
        return result;
    }
}
