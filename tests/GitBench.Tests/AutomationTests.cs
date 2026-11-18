using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Automation;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>Shared helpers for the automation tests.</summary>
public abstract class AutomationTestBase(TestApp app)
{
    protected readonly TestApp App = app;

    protected async Task<AutomationRule> RuleAsync(int projectId, AutomationTrigger trigger, RuleDefinition def, string actor = "alice",
        AutomationRunAs runAs = AutomationRunAs.AutomationBot, bool allowRuleTriggers = false, string name = "Test rule")
    {
        await using var s = App.Scope(App.UserId(actor));
        return await s.Get<AutomationService>().SaveAsync(RuleOwner.Project(projectId), null, name, true, trigger, def, runAs, allowRuleTriggers);
    }

    protected async Task IdleAsync() =>
        Assert.True(await App.Services.GetRequiredService<AutomationQueue>().WaitIdleAsync(TimeSpan.FromSeconds(20)), "automation queue didn't drain");

    protected List<AutomationRun> Runs(int ruleId)
    {
        using var db = App.Db();
        return db.AutomationRuns.AsNoTracking().Where(r => r.RuleId == ruleId).OrderBy(r => r.Id).ToList();
    }

    protected Issue Load(int issueId)
    {
        using var db = App.Db();
        return db.Issues.AsNoTracking().Include(i => i.Labels).Include(i => i.WorkflowStatus).Include(i => i.Comments).ThenInclude(c => c.Author)
            .Include(i => i.Activities).ThenInclude(a => a.Actor).AsSplitQuery().First(i => i.Id == issueId);
    }

    protected int BotId()
    {
        using var db = App.Db();
        return db.Users.Where(u => u.UserName == AutomationBot.UserName).Select(u => u.Id).First();
    }

    protected async Task<T> As<T>(string user, Func<TestScope, Task<T>> work)
    {
        await using var s = App.Scope(App.UserId(user));
        return await work(s);
    }

    protected async Task As(string user, Func<TestScope, Task> work)
    {
        await using var s = App.Scope(App.UserId(user));
        await work(s);
    }

    protected static RuleDefinition Def(params ActionDef[] actions) => new() { Actions = [.. actions] };
}

public class AutomationTests(TestApp app) : AutomationTestBase(app), IClassFixture<TestApp>
{
    private Task<Project> ProjectAsync() =>
        App.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer), ("dan", ProjectRole.Viewer));

    // ----- Triggers -----

    [Fact]
    public async Task Issue_created_round_robin_assigns_new_bugs_only()
    {
        var project = await ProjectAsync();
        var template = AutomationService.Templates.First(t => t.Name.StartsWith("Auto-assign"));
        var opts = await As("alice", s => s.Get<AutomationService>().GetEditorOptionsAsync(RuleOwner.Project(project.Id)));
        var rule = await RuleAsync(project.Id, template.Trigger, template.Build(opts));

        var bug1 = await App.CreateIssueAsync(project.Id, "bob", "Crash 1", IssueType.Bug);
        var bug2 = await App.CreateIssueAsync(project.Id, "bob", "Crash 2", IssueType.Bug);
        var task = await App.CreateIssueAsync(project.Id, "bob", "Not a bug", IssueType.Task);
        await IdleAsync();

        // Developers and above: alice (Owner), bob, carol; dan is a Viewer and never picked.
        var devs = new[] { "alice", "bob", "carol" }.Select(App.UserId).Order().ToList();
        var a1 = Load(bug1.Id).AssigneeId;
        Assert.True(a1 != null, string.Join(" | ", Runs(rule.Id).Select(r => $"{r.Status} {r.IssueId} {r.Message} {r.Actions}")));
        var a2 = Load(bug2.Id).AssigneeId;
        Assert.Equal(devs[0], a1);
        Assert.Equal(devs[1], a2);
        Assert.Null(Load(task.Id).AssigneeId);
        var runs = Runs(rule.Id);
        Assert.Equal(2, runs.Count(r => r.Status == AutomationRunStatus.Success));
        Assert.Contains(runs, r => r.Status == AutomationRunStatus.NoActions && r.IssueId == task.Id);
        // The change is the bot's, with an activity entry like a person's.
        Assert.Contains(Load(bug1.Id).Activities, a => a.ActorId == BotId() && a.Text.StartsWith("assigned to"));
    }

    [Fact]
    public async Task Issue_transitioned_respects_from_and_to_and_renders_smart_values()
    {
        var project = await ProjectAsync();
        var def = Def(new ActionDef { Type = AutomationActionType.Comment, Text = "{{issue.key}} moved by {{initiator.name}} to {{issue.status}}" });
        def.Trigger = new TriggerOptions { FromStatus = "To Do", ToStatus = "in progress" };
        var rule = await RuleAsync(project.Id, AutomationTrigger.IssueTransitioned, def);
        var issue = await App.CreateIssueAsync(project.Id, "bob");

        await As("bob", s => s.Get<IssueService>().SetStatusAsync(issue.Id, IssueStatus.InProgress, App.UserId("bob")));
        await IdleAsync();
        var comment = Assert.Single(Load(issue.Id).Comments);
        Assert.Equal($"{project.Key}-{issue.Number} moved by Bob Martinez to In Progress", comment.Body);
        Assert.Equal(AutomationBot.DisplayName, comment.Author.DisplayName);

        // In Progress → Done doesn't match "from To Do".
        await As("bob", s => s.Get<IssueService>().SetStatusAsync(issue.Id, IssueStatus.Done, App.UserId("bob")));
        await IdleAsync();
        Assert.Single(Load(issue.Id).Comments);
        Assert.Single(Runs(rule.Id));
    }

    [Fact]
    public async Task Field_changed_fires_for_watched_fields_only()
    {
        var project = await ProjectAsync();
        var def = Def(new ActionDef { Type = AutomationActionType.AddLabel, Label = "BUG" });
        def.Trigger = new TriggerOptions { Fields = IssueField.Priority };
        var rule = await RuleAsync(project.Id, AutomationTrigger.FieldChanged, def);
        var issue = await App.CreateIssueAsync(project.Id, "bob");
        var bob = App.UserId("bob");

        await As("bob", s => s.Get<IssueService>().UpdateIssueAsync(issue.Id, bob, (i, _) => { i.AssigneeId = bob; return Task.FromResult<string?>("assigned"); }));
        await IdleAsync();
        Assert.Empty(Runs(rule.Id));

        await As("bob", s => s.Get<IssueService>().UpdateIssueAsync(issue.Id, bob, (i, _) => { i.Priority = IssuePriority.High; return Task.FromResult<string?>("prio"); }));
        await IdleAsync();
        Assert.Equal("bug", Assert.Single(Load(issue.Id).Labels).Name);
        Assert.Equal(AutomationRunStatus.Success, Assert.Single(Runs(rule.Id)).Status);
    }

    [Fact]
    public async Task Comment_added_and_conditions_any_vs_all()
    {
        var project = await ProjectAsync();
        var def = Def(new ActionDef { Type = AutomationActionType.SetPriority, Priority = IssuePriority.Highest });
        def.MatchAll = false;
        def.Conditions =
        [
            new ConditionDef { Field = "type", Operator = "is", Value = "Bug" },
            new ConditionDef { Field = "title", Operator = "contains", Value = "urgent" },
        ];
        var rule = await RuleAsync(project.Id, AutomationTrigger.CommentAdded, def);
        var task = await App.CreateIssueAsync(project.Id, "bob", "Something URGENT");
        var other = await App.CreateIssueAsync(project.Id, "bob", "Calm task");
        var bob = App.UserId("bob");

        await As("bob", s => s.Get<IssueService>().AddCommentAsync(task.Id, bob, "hello"));
        await As("bob", s => s.Get<IssueService>().AddCommentAsync(other.Id, bob, "hello"));
        await IdleAsync();
        Assert.Equal(IssuePriority.Highest, Load(task.Id).Priority); // any: the title matched
        Assert.Equal(IssuePriority.Medium, Load(other.Id).Priority);
        Assert.Equal(2, Runs(rule.Id).Count);
    }

    [Fact]
    public async Task Pull_request_triggers_act_on_linked_issues()
    {
        var project = await ProjectAsync();
        var opts = await As("alice", s => s.Get<AutomationService>().GetEditorOptionsAsync(RuleOwner.Project(project.Id)));
        var template = AutomationService.Templates.First(t => t.Trigger == AutomationTrigger.PullRequestMerged);
        var merged = await RuleAsync(project.Id, template.Trigger, template.Build(opts), name: "merged");
        var opened = await RuleAsync(project.Id, AutomationTrigger.PullRequestOpened,
            Def(new ActionDef { Type = AutomationActionType.Comment, Text = "PR opened: {{pr.title}}" }), name: "opened");
        var declined = await RuleAsync(project.Id, AutomationTrigger.PullRequestDeclined,
            Def(new ActionDef { Type = AutomationActionType.Comment, Text = "declined" }), name: "declined");

        var issue1 = await App.CreateIssueAsync(project.Id, "bob", "One");
        var issue2 = await App.CreateIssueAsync(project.Id, "bob", "Two");
        var unrelated = await App.CreateIssueAsync(project.Id, "bob", "Three");
        var repo = await App.CreateRepoAsync(project.Id);
        var branch = $"feature/{project.Key}-{issue1.Number}-thing";
        App.Commit(repo, branch, "a.txt", "a");

        var alice = App.User("alice");
        var pr = await As("alice", s => s.Get<PullRequestService>().CreateAsync(repo, $"Fix {project.Key}-{issue2.Number}", null, branch, "main", alice.Id));
        await IdleAsync();
        Assert.Contains(Load(issue1.Id).Comments, c => c.Body == $"PR opened: Fix {project.Key}-{issue2.Number}");
        Assert.Contains(Load(issue2.Id).Comments, c => c.Body.StartsWith("PR opened"));
        Assert.Empty(Load(unrelated.Id).Comments);
        Assert.Equal(AutomationRunStatus.Success, Assert.Single(Runs(opened.Id)).Status);

        var outcome = await As("alice", s => s.Get<PullRequestService>().MergeAsync(pr.Id, alice, new MergeRequest()));
        Assert.True(outcome.Success);
        await IdleAsync();
        Assert.Equal(IssueStatus.Done, Load(issue1.Id).Status);
        Assert.Equal(IssueStatus.Done, Load(issue2.Id).Status);
        Assert.Equal(IssueStatus.Todo, Load(unrelated.Id).Status);
        var run = Assert.Single(Runs(merged.Id));
        Assert.Equal(AutomationRunStatus.Success, run.Status);
        Assert.Contains($"Transitioned {project.Key}-{issue1.Number} to Done", run.Actions);
        Assert.Empty(Runs(declined.Id));

        // Declined: a second PR without linked issues runs once with no issue, so the comment action fails cleanly.
        App.Commit(repo, "other", "b.txt", "b");
        var pr2 = await As("alice", s => s.Get<PullRequestService>().CreateAsync(repo, "No keys here", null, "other", "main", alice.Id));
        await As("alice", s => s.Get<PullRequestService>().DeclineAsync(pr2.Id, alice.Id));
        await IdleAsync();
        var drun = Assert.Single(Runs(declined.Id));
        Assert.Equal(AutomationRunStatus.Failure, drun.Status);
        Assert.Contains("needs an issue", drun.Message);
    }

    [Fact]
    public async Task Pipeline_failed_and_passed_triggers()
    {
        var project = await ProjectAsync();
        var failed = await RuleAsync(project.Id, AutomationTrigger.PipelineFailed,
            Def(new ActionDef { Type = AutomationActionType.Comment, Text = "Pipeline {{pipeline.status}} on {{pipeline.ref}}" }), name: "failed");
        var passed = await RuleAsync(project.Id, AutomationTrigger.PipelinePassed,
            Def(new ActionDef { Type = AutomationActionType.AddLabel, Label = "documentation" }), name: "passed");
        var issue = await App.CreateIssueAsync(project.Id, "bob");
        var repo = await App.CreateRepoAsync(project.Id);
        int pipelineId;
        await using (var db = App.Db())
        {
            var p = new Pipeline { RepositoryId = repo.Id, Ref = $"{project.Key}-{issue.Number}-fix", CommitSha = new string('a', 40), Status = PipelineStatus.Failed, TriggeredById = App.UserId("bob") };
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
            pipelineId = p.Id;
        }
        // What CiRunner queues when a pipeline finishes.
        App.Services.GetRequiredService<AutomationQueue>().Enqueue(
            new AutomationEvent(AutomationEventKind.PipelineCompleted, App.UserId("bob")) { PipelineId = pipelineId, PipelinePassed = false });
        await IdleAsync();
        Assert.Equal($"Pipeline failed on {project.Key}-{issue.Number}-fix", Assert.Single(Load(issue.Id).Comments).Body);
        Assert.Empty(Runs(passed.Id));

        App.Services.GetRequiredService<AutomationQueue>().Enqueue(
            new AutomationEvent(AutomationEventKind.PipelineCompleted, App.UserId("bob")) { PipelineId = pipelineId, PipelinePassed = true });
        await IdleAsync();
        Assert.Contains(Load(issue.Id).Labels, l => l.Name == "documentation");
        Assert.Single(Runs(failed.Id));
    }

    [Fact]
    public async Task Manual_trigger_runs_on_the_issue_for_developers_only()
    {
        var project = await ProjectAsync();
        var rule = await RuleAsync(project.Id, AutomationTrigger.Manual,
            Def(new ActionDef { Type = AutomationActionType.CreateSubtask, Text = "Review {{issue.title}}" },
                new ActionDef { Type = AutomationActionType.Assign, Assign = AssignMode.TriggerUser }));
        var issue = await App.CreateIssueAsync(project.Id, "carol", "Release notes");

        var run = await As("bob", async s =>
        {
            var svc = s.Get<AutomationService>();
            Assert.Contains(await svc.ManualRulesForIssueAsync(issue.Id), r => r.Id == rule.Id);
            return await svc.RunManualAsync(rule.Id, issue.Id);
        });
        Assert.NotNull(run);
        Assert.Equal(AutomationRunStatus.Success, run.Status);
        Assert.Equal(App.UserId("bob"), Load(issue.Id).AssigneeId);
        await using (var db = App.Db())
        {
            var child = db.Issues.Single(i => i.ParentId == issue.Id);
            Assert.Equal(IssueType.Subtask, child.Type);
            Assert.Equal("Review Release notes", child.Title);
        }

        // dan is a Viewer: no manual rules offered, and running one is refused.
        await As("dan", async s =>
        {
            Assert.Empty(await s.Get<AutomationService>().ManualRulesForIssueAsync(issue.Id));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Get<AutomationService>().RunManualAsync(rule.Id, issue.Id));
        });
    }

    // ----- Actions: workflow, permissions -----

    [Fact]
    public async Task Transition_action_respects_the_workflow()
    {
        var project = await ProjectAsync();
        var wf = await As("alice", s => s.Get<WorkflowService>().GetAsync(project.Id));
        await As("alice", s => s.Get<WorkflowService>().SetTransitionsAsync(project.Id, new Dictionary<int, HashSet<int>>
        {
            [wf.FirstOf(IssueStatus.Todo)!.Id] = [wf.FirstOf(IssueStatus.InProgress)!.Id],
        }));
        var rule = await RuleAsync(project.Id, AutomationTrigger.IssueCreated,
            Def(new ActionDef { Type = AutomationActionType.Transition, Status = "Done" },
                new ActionDef { Type = AutomationActionType.Comment, Text = "never" }));
        var issue = await App.CreateIssueAsync(project.Id, "bob");
        await IdleAsync();

        var run = Assert.Single(Runs(rule.Id));
        Assert.Equal(AutomationRunStatus.Failure, run.Status);
        Assert.Contains("doesn't allow", run.Message);
        Assert.Equal(IssueStatus.Todo, Load(issue.Id).Status);
        Assert.Empty(Load(issue.Id).Comments); // stops at the first failing action
    }

    [Fact]
    public async Task Rules_need_maintainer_and_run_as_author_uses_their_permissions()
    {
        var project = await ProjectAsync();
        await As("bob", async s => await Assert.ThrowsAsync<ForbiddenException>(() =>
            s.Get<AutomationService>().SaveAsync(RuleOwner.Project(project.Id), null, "x", true, AutomationTrigger.IssueCreated,
                Def(new ActionDef { Type = AutomationActionType.SetPriority, Priority = IssuePriority.High }), AutomationRunAs.AutomationBot, false)));

        var rule = await RuleAsync(project.Id, AutomationTrigger.IssueCreated,
            Def(new ActionDef { Type = AutomationActionType.SetPriority, Priority = IssuePriority.High }), actor: "carol", runAs: AutomationRunAs.RuleAuthor);
        var first = await App.CreateIssueAsync(project.Id, "bob");
        await IdleAsync();
        Assert.Equal(IssuePriority.High, Load(first.Id).Priority);
        Assert.Contains(Load(first.Id).Activities, a => a.ActorId == App.UserId("carol") && a.Text.Contains("priority"));

        // carol is demoted to Viewer: her rule can no longer edit issues.
        await As("alice", s => s.Get<MemberService>().ChangeRoleAsync(project.Id, App.UserId("carol"), ProjectRole.Viewer));
        var second = await App.CreateIssueAsync(project.Id, "bob");
        await IdleAsync();
        Assert.Equal(IssuePriority.Medium, Load(second.Id).Priority);
        var run = Runs(rule.Id).Last();
        Assert.Equal(AutomationRunStatus.Failure, run.Status);
        Assert.Contains("author", run.Message);
    }

    [Fact]
    public async Task Bot_can_only_link_within_the_project_and_assign_project_users()
    {
        var project = await ProjectAsync();
        var other = await App.CreateProjectAsync("alice");
        var foreign = await App.CreateIssueAsync(other.Id, "alice");
        var target = await App.CreateIssueAsync(project.Id, "bob", "Target");
        var outsider = await App.CreateUserAsync("outsider" + Guid.NewGuid().ToString("N")[..6]);

        var link = await RuleAsync(project.Id, AutomationTrigger.Manual,
            Def(new ActionDef { Type = AutomationActionType.LinkIssue, LinkType = IssueLinkRelation.Blocks, IssueKey = $"{project.Key}-{target.Number}" }), name: "link ok");
        var linkForeign = await RuleAsync(project.Id, AutomationTrigger.Manual,
            Def(new ActionDef { Type = AutomationActionType.LinkIssue, IssueKey = $"{other.Key}-{foreign.Number}" }), name: "link foreign");
        var assignOutsider = await RuleAsync(project.Id, AutomationTrigger.Manual,
            Def(new ActionDef { Type = AutomationActionType.Assign, Assign = AssignMode.User, UserId = outsider.Id }), name: "assign outsider");
        var issue = await App.CreateIssueAsync(project.Id, "bob");

        var ok = await As("bob", s => s.Get<AutomationService>().RunManualAsync(link.Id, issue.Id));
        Assert.Equal(AutomationRunStatus.Success, ok!.Status);
        await using (var db = App.Db())
            Assert.True(db.IssueLinks.Any(l => l.SourceIssueId == issue.Id && l.TargetIssueId == target.Id && l.Type == IssueLinkType.Blocks));

        var bad = await As("bob", s => s.Get<AutomationService>().RunManualAsync(linkForeign.Id, issue.Id));
        Assert.Equal(AutomationRunStatus.Failure, bad!.Status);
        Assert.Contains("another project", bad.Message);

        var bad2 = await As("bob", s => s.Get<AutomationService>().RunManualAsync(assignOutsider.Id, issue.Id));
        Assert.Equal(AutomationRunStatus.Failure, bad2!.Status);
        Assert.Null(Load(issue.Id).AssigneeId);
    }

    [Fact]
    public async Task All_subtasks_done_closes_the_parent()
    {
        var project = await ProjectAsync();
        var opts = await As("alice", s => s.Get<AutomationService>().GetEditorOptionsAsync(RuleOwner.Project(project.Id)));
        var template = AutomationService.Templates.First(t => t.Trigger == AutomationTrigger.IssueTransitioned);
        var rule = await RuleAsync(project.Id, template.Trigger, template.Build(opts));
        var parent = await App.CreateIssueAsync(project.Id, "bob", "Story", IssueType.Story);
        var s1 = await App.CreateIssueAsync(project.Id, "bob", "Sub 1", IssueType.Subtask, parentId: parent.Id);
        var s2 = await App.CreateIssueAsync(project.Id, "bob", "Sub 2", IssueType.Subtask, parentId: parent.Id);
        var bob = App.UserId("bob");

        await As("bob", s => s.Get<IssueService>().SetStatusAsync(s1.Id, IssueStatus.Done, bob));
        await IdleAsync();
        Assert.Equal(IssueStatus.Todo, Load(parent.Id).Status);
        Assert.Contains("Not all children", Runs(rule.Id).Last().Actions);

        await As("bob", s => s.Get<IssueService>().SetStatusAsync(s2.Id, IssueStatus.Done, bob));
        await IdleAsync();
        Assert.Equal(IssueStatus.Done, Load(parent.Id).Status);
    }

    [Fact]
    public async Task Linked_pr_and_subtask_conditions()
    {
        var project = await ProjectAsync();
        var def = Def(new ActionDef { Type = AutomationActionType.AddLabel, Label = "enhancement" });
        def.Conditions =
        [
            new ConditionDef { Field = "linkedPullRequest", Operator = "openExists" },
            new ConditionDef { Field = "subtasksDone", Operator = "notAllDone" },
            new ConditionDef { Field = "label", Operator = "notContains", Value = "enhancement" },
        ];
        var rule = await RuleAsync(project.Id, AutomationTrigger.Manual, def);
        var withPr = await App.CreateIssueAsync(project.Id, "bob", "Has PR", IssueType.Story);
        var withoutPr = await App.CreateIssueAsync(project.Id, "bob", "No PR", IssueType.Story);
        var repo = await App.CreateRepoAsync(project.Id);
        App.Commit(repo, $"{project.Key}-{withPr.Number}-work", "x.txt", "x");
        // A PR mentioning WEB-1x must not count for WEB-1 (exact key match).
        await As("alice", s => s.Get<PullRequestService>().CreateAsync(repo, "Work", null, $"{project.Key}-{withPr.Number}-work", "main", App.UserId("alice")));

        Assert.Equal(AutomationRunStatus.Success, (await As("bob", s => s.Get<AutomationService>().RunManualAsync(rule.Id, withPr.Id)))!.Status);
        Assert.Equal(AutomationRunStatus.NoActions, (await As("bob", s => s.Get<AutomationService>().RunManualAsync(rule.Id, withoutPr.Id)))!.Status);
        Assert.Contains(Load(withPr.Id).Labels, l => l.Name == "enhancement");
    }

    [Fact]
    public async Task Notify_action_reaches_the_user()
    {
        var project = await ProjectAsync();
        var rule = await RuleAsync(project.Id, AutomationTrigger.IssueCreated,
            Def(new ActionDef { Type = AutomationActionType.Notify, Notify = NotifyTarget.User, UserId = App.UserId("carol"), Text = "New: {{issue.key}}" }));
        var issue = await App.CreateIssueAsync(project.Id, "bob");
        await IdleAsync();
        await using var db = App.Db();
        var n = db.Notifications.Single(x => x.UserId == App.UserId("carol") && x.Kind == NotificationKind.Automation && x.ProjectId == project.Id);
        Assert.Equal($"New: {project.Key}-{issue.Number}", n.Title);
        Assert.Equal(BotId(), n.ActorId);
        Assert.Equal(AutomationRunStatus.Success, Assert.Single(Runs(rule.Id)).Status);
    }

    [Fact]
    public async Task Webhook_action_posts_through_the_safe_sender()
    {
        var project = await ProjectAsync();
        using var receiver = new WebhookReceiver();
        await RuleAsync(project.Id, AutomationTrigger.IssueCreated, Def(new ActionDef { Type = AutomationActionType.SendWebhook, Url = receiver.Url }));
        var issue = await App.CreateIssueAsync(project.Id, "bob", "Hooked");
        var got = await receiver.WaitForAsync("automation");
        using var json = JsonDocument.Parse(got.Body);
        Assert.Equal($"{project.Key}-{issue.Number}", json.RootElement.GetProperty("issue").GetProperty("key").GetString());
        Assert.Equal("Test rule", json.RootElement.GetProperty("rule").GetProperty("name").GetString());

        // Invalid URLs are refused when saving.
        await As("alice", async s => await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Get<AutomationService>().SaveAsync(
            RuleOwner.Project(project.Id), null, "bad", true, AutomationTrigger.IssueCreated,
            Def(new ActionDef { Type = AutomationActionType.SendWebhook, Url = "ftp://example.com" }), AutomationRunAs.AutomationBot, false)));
    }

    // ----- Loop protection -----

    [Fact]
    public async Task Bot_changes_do_not_retrigger_rules_by_default()
    {
        var project = await ProjectAsync();
        var rule = await RuleAsync(project.Id, AutomationTrigger.CommentAdded, Def(new ActionDef { Type = AutomationActionType.Comment, Text = "echo" }));
        var issue = await App.CreateIssueAsync(project.Id, "bob");
        await As("bob", s => s.Get<IssueService>().AddCommentAsync(issue.Id, App.UserId("bob"), "hi"));
        await IdleAsync();
        Assert.Equal(2, Load(issue.Id).Comments.Count); // bob's and one echo
        Assert.Single(Runs(rule.Id));
    }

    [Fact]
    public async Task Rule_chains_stop_at_the_depth_limit()
    {
        var project = await ProjectAsync();
        var rule = await RuleAsync(project.Id, AutomationTrigger.CommentAdded, Def(new ActionDef { Type = AutomationActionType.Comment, Text = "echo" }),
            allowRuleTriggers: true);
        var issue = await App.CreateIssueAsync(project.Id, "bob");
        await As("bob", s => s.Get<IssueService>().AddCommentAsync(issue.Id, App.UserId("bob"), "hi"));
        await IdleAsync();
        // Depths 0..3 each add one echo; the comment made at depth 3 would start depth 4 and is stopped.
        Assert.Equal(1 + 4, Load(issue.Id).Comments.Count);
        var runs = Runs(rule.Id);
        Assert.Equal(4, runs.Count(r => r.Status == AutomationRunStatus.Success));
        Assert.Equal([0, 1, 2, 3], runs.Where(r => r.Status == AutomationRunStatus.Success).Select(r => r.Depth));
        Assert.Single(runs, r => r.Status == AutomationRunStatus.LoopPrevented);
    }

    // ----- Scheduled -----

    [Fact]
    public void Schedule_arithmetic()
    {
        var daily = new TriggerOptions { Schedule = ScheduleKind.Daily, TimeOfDay = "09:30" };
        var created = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(AutomationSchedule.IsDue(daily, null, created, created.AddHours(5)));              // 15:00, the 09:30 slot was before creation
        Assert.True(AutomationSchedule.IsDue(daily, null, created, new DateTime(2030, 1, 2, 9, 30, 0))); // next morning
        Assert.False(AutomationSchedule.IsDue(daily, new DateTime(2030, 1, 2, 9, 30, 0), created, new DateTime(2030, 1, 2, 23, 0, 0)));

        var weekly = new TriggerOptions { Schedule = ScheduleKind.Weekly, DayOfWeek = DayOfWeek.Friday, TimeOfDay = "17:00" }; // 2030-01-04 is a Friday
        Assert.Equal(new DateTime(2030, 1, 4, 17, 0, 0), AutomationSchedule.PreviousOccurrence(weekly, new DateTime(2030, 1, 10, 12, 0, 0)));
        Assert.False(AutomationSchedule.IsDue(weekly, null, created, new DateTime(2030, 1, 4, 16, 59, 0)));
        Assert.True(AutomationSchedule.IsDue(weekly, null, created, new DateTime(2030, 1, 4, 17, 0, 0)));

        var hourly = new TriggerOptions { Schedule = ScheduleKind.EveryHours, Hours = 6 };
        Assert.False(AutomationSchedule.IsDue(hourly, created, created, created.AddHours(5)));
        Assert.True(AutomationSchedule.IsDue(hourly, created, created, created.AddHours(6)));
        Assert.NotNull(AutomationSchedule.Validate(new TriggerOptions { Schedule = ScheduleKind.Daily, TimeOfDay = "25:00" }));
    }

    [Fact]
    public async Task Scheduled_rule_runs_when_due_on_matching_open_issues()
    {
        var project = await ProjectAsync();
        var def = Def(new ActionDef { Type = AutomationActionType.Comment, Text = "Daily nudge for {{issue.key}}" });
        def.Trigger = new TriggerOptions { Schedule = ScheduleKind.Daily, TimeOfDay = "09:00", Scope = ScheduleScope.OpenIssues };
        def.Conditions = [new ConditionDef { Field = "priority", Operator = "atLeast", Value = "High" }];
        var rule = await RuleAsync(project.Id, AutomationTrigger.Scheduled, def);
        var urgent = await App.CreateIssueAsync(project.Id, "bob", "Urgent");
        var calm = await App.CreateIssueAsync(project.Id, "bob", "Calm");
        await using (var db = App.Db())
        {
            await db.Issues.Where(i => i.Id == urgent.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Priority, IssuePriority.Highest));
            // A fake clock: pretend the rule was saved at midnight on 2030-01-01 (the real clock never reaches it during the test).
            await db.AutomationRules.Where(r => r.Id == rule.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, new DateTime(2030, 1, 1)));
        }
        var engine = App.Services.GetRequiredService<AutomationEngine>();

        await engine.RunDueScheduledAsync(new DateTime(2030, 1, 1, 8, 59, 0));
        Assert.Empty(Runs(rule.Id));
        await engine.RunDueScheduledAsync(new DateTime(2030, 1, 1, 9, 0, 30));
        await engine.RunDueScheduledAsync(new DateTime(2030, 1, 1, 15, 0, 0)); // same day: not again
        await IdleAsync();
        var run = Assert.Single(Runs(rule.Id));
        Assert.Equal(AutomationRunStatus.Success, run.Status);
        Assert.StartsWith("Scheduled", run.Trigger);
        Assert.Equal($"Daily nudge for {project.Key}-{urgent.Number}", Assert.Single(Load(urgent.Id).Comments).Body);
        Assert.Empty(Load(calm.Id).Comments);

        await engine.RunDueScheduledAsync(new DateTime(2030, 1, 2, 9, 1, 0));
        Assert.Equal(2, Runs(rule.Id).Count);
        Assert.Equal(2, Load(urgent.Id).Comments.Count);
    }

    // ----- Groups, audit, smart values -----

    [Fact]
    public async Task Group_rules_apply_to_projects_below_the_group()
    {
        int acme;
        await using (var db = App.Db()) acme = db.Groups.Where(g => g.Path == "acme").Select(g => g.Id).First();
        var web = App.ProjectId("WEB");
        await As("alice", s => s.Get<AutomationService>().SaveAsync(RuleOwner.Group(acme), null, "group rule", true, AutomationTrigger.IssueCreated,
            Def(new ActionDef { Type = AutomationActionType.SetPriority, Priority = IssuePriority.Lowest }), AutomationRunAs.AutomationBot, false));
        var issue = await App.CreateIssueAsync(web, "alice", "In WEB");
        var outside = await App.CreateIssueAsync((await ProjectAsync()).Id, "alice", "Top-level project");
        await IdleAsync();
        Assert.Equal(IssuePriority.Lowest, Load(issue.Id).Priority);
        Assert.Equal(IssuePriority.Medium, Load(outside.Id).Priority);
        Assert.Single(await As("alice", s => s.Get<AutomationService>().InheritedAsync(web)));
        // dan is only a Developer in Acme: he can't manage its rules.
        await As("dan", async s => Assert.False(await s.Get<AutomationService>().CanManageAsync(RuleOwner.Group(acme))));
    }

    [Fact]
    public async Task Rule_changes_are_audited()
    {
        var project = await ProjectAsync();
        var rule = await RuleAsync(project.Id, AutomationTrigger.Manual, Def(new ActionDef { Type = AutomationActionType.Comment, Text = "x" }));
        await As("alice", async s =>
        {
            await s.Get<AutomationService>().SetEnabledAsync(rule.Id, false);
            await s.Get<AutomationService>().DeleteAsync(rule.Id);
        });
        await using var db = App.Db();
        var actions = db.AuditEvents.Where(e => e.ProjectId == project.Id && e.Action.StartsWith("automation.")).Select(e => e.Action).ToList();
        Assert.Equal(["automation.rule_created", "automation.rule_disabled", "automation.rule_deleted"], actions);
    }

    [Fact]
    public void Smart_values_render_and_unknown_names_are_empty()
    {
        var project = new Project { Key = "ABC", Name = "Abc" };
        var issue = new Issue { Number = 7, Title = "Fix it", Priority = IssuePriority.High, Project = project, Labels = [new Label { Name = "ui" }] };
        var ctx = new AutomationContext
        {
            Rule = new AutomationRule { Name = "R" }, Definition = new RuleDefinition(), Project = project, Issue = issue,
            Event = new AutomationEvent(AutomationEventKind.Manual, 1), Initiator = new User { DisplayName = "Ann", UserName = "ann" },
        };
        Assert.Equal("ABC-7 Fix it (High) by Ann [ui] ·  · R",
            SmartValues.Render("{{issue.key}} {{ issue.title }} ({{issue.priority}}) by {{initiator.name}} [{{issue.labels}}] · {{nope}} · {{rule.name}}", ctx, DateTime.UtcNow));
    }
}

/// <summary>An app whose rules may run only three times an hour.</summary>
public class LowRateLimitApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings) => settings["Automation:MaxRunsPerRulePerHour"] = "3";
}

public class AutomationRateLimitTests(LowRateLimitApp app) : AutomationTestBase(app), IClassFixture<LowRateLimitApp>
{
    [Fact]
    public async Task Runs_beyond_the_hourly_limit_are_throttled_and_logged_once()
    {
        var project = await App.CreateProjectAsync("alice");
        var rule = await RuleAsync(project.Id, AutomationTrigger.IssueCreated, Def(new ActionDef { Type = AutomationActionType.SetPriority, Priority = IssuePriority.High }));
        var issues = new List<Issue>();
        for (var i = 0; i < 6; i++) issues.Add(await App.CreateIssueAsync(project.Id, "alice", $"Issue {i}"));
        await IdleAsync();
        var runs = Runs(rule.Id);
        Assert.Equal(3, runs.Count(r => r.Status == AutomationRunStatus.Success));
        Assert.Single(runs, r => r.Status == AutomationRunStatus.Throttled);
        Assert.Equal(3, issues.Count(i => Load(i.Id).Priority == IssuePriority.High));
    }
}
