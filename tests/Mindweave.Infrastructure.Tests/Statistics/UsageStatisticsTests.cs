using Mindweave.Application.Statistics;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Testing;
using Mindweave.Testing.Usage;

namespace Mindweave.Infrastructure.Tests.Statistics;

/// <summary>
/// The statistics page's figures (2026-11-12) through the real SQLite aggregates and the service that buckets them:
/// local days in an explicit zone (across a daylight-saving change), the split by model and host and by purpose, usage
/// the service did not report counted apart, the workspace scope, and the activity facts and milestones.
/// </summary>
public class UsageStatisticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Central European time without depending on the machine's zone database: UTC+1, and UTC+2 from the last Sunday of
    /// March 02:00 to the last Sunday of October 03:00 (in 2026: 29 March and 25 October).
    /// </summary>
    internal static readonly TimeZoneInfo Berlin = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Berlin",
        TimeSpan.FromHours(1),
        "Test Berlin",
        "Test Berlin",
        "Test Berlin Summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    /// <summary>A local time in <see cref="Berlin"/>.</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Berlin.GetUtcOffset(local));
    }

    private static UsageStatisticsService Service(ChatHarness h, DateTimeOffset now) =>
        new(new SqliteStatisticsStore(h.Database), h.Settings, TimeProvider.System, new UsageStatisticsOptions { Time = new ManualTime(now), Zone = Berlin });

    [Fact]
    public async Task Days_are_local_days_of_the_zone_across_the_spring_change()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var c = await seed.ConversationAsync(h.Workspace, Local(2026, 3, 1, 9));

        // 22:30 UTC on both evenings: 23:30 on the 28th (UTC+1), but 00:30 on the 30th (UTC+2 since the 29th).
        await seed.RequestAsync(c, "answer", "m", new DateTimeOffset(2026, 3, 28, 22, 30, 0, TimeSpan.Zero), 100, 10);
        await seed.RequestAsync(c, "answer", "m", new DateTimeOffset(2026, 3, 29, 22, 30, 0, TimeSpan.Zero), 200, 20);
        await seed.RequestAsync(c, "answer", "m", Local(2026, 3, 29, 23, 30), 400, 40);

        // Just before the window (the 2nd, local midnight) and just inside it.
        await seed.RequestAsync(c, "answer", "m", Local(2026, 3, 1, 23, 59), 1, 1);
        await seed.RequestAsync(c, "answer", "m", Local(2026, 3, 2, 0, 0), 8, 0);

        var report = (await Service(h, Local(2026, 3, 31, 12)).LoadAsync(null, UsagePeriod.Daily, Ct)).Usage;

        Assert.Equal(30, report.Buckets.Count);
        Assert.Equal((new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 31)), (report.From, report.To));
        Assert.Equal(new DateOnly(2026, 3, 2), report.Buckets[0].Start);
        long InputOn(int day) => report.Buckets.Single(b => b.Start == new DateOnly(2026, 3, day)).Counts.Input;
        Assert.Equal(100, InputOn(28));
        Assert.Equal(400, InputOn(29));
        Assert.Equal(200, InputOn(30));
        Assert.Equal(8, InputOn(2));
        Assert.Equal(0, InputOn(31));
        Assert.Equal(new TokenCounts(708, 70, 4, 0), report.Totals);
    }

    [Fact]
    public async Task Months_are_local_months_across_the_autumn_change()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var c = await seed.ConversationAsync(h.Workspace, Local(2025, 1, 1, 9));

        // 23:30 UTC on 31 October 2026 is already 1 November in winter time (UTC+1); 22:30 UTC is still October.
        await seed.RequestAsync(c, "answer", "m", new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero), 1000, 100);
        await seed.RequestAsync(c, "answer", "m", new DateTimeOffset(2026, 10, 31, 22, 30, 0, TimeSpan.Zero), 10, 1);
        await seed.RequestAsync(c, "title", "m", Local(2026, 3, 15, 12), 5, 5);

        // The first month of the window starts on 1 December 2025; November 2025 is out.
        await seed.RequestAsync(c, "answer", "m", Local(2025, 11, 30, 23, 59), 7, 7);
        await seed.RequestAsync(c, "answer", "m", Local(2025, 12, 1, 0, 0), 3, 0);

        var report = (await Service(h, Local(2026, 11, 12, 10)).LoadAsync(null, UsagePeriod.Monthly, Ct)).Usage;

        Assert.Equal(12, report.Buckets.Count);
        Assert.Equal(new DateOnly(2025, 12, 1), report.Buckets[0].Start);
        Assert.Equal(new DateOnly(2026, 11, 1), report.Buckets[^1].Start);
        long InputIn(int year, int month) => report.Buckets.Single(b => b.Start == new DateOnly(year, month, 1)).Counts.Input;
        Assert.Equal(1000, InputIn(2026, 11));
        Assert.Equal(10, InputIn(2026, 10));
        Assert.Equal(5, InputIn(2026, 3));
        Assert.Equal(3, InputIn(2025, 12));
        Assert.Equal(0, InputIn(2026, 1));
        Assert.Equal(1018, report.Totals.Input);
    }

    [Fact]
    public async Task Models_are_told_apart_by_name_and_host_and_purposes_include_workspace_names_recommendations_and_link_suggestions()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var now = Local(2026, 11, 12, 10);
        var c = await seed.ConversationAsync(h.Workspace, now.AddDays(-3));
        await seed.RequestAsync(c, "answer", "deepseek-flash", now.AddHours(-5), 1000, 300, "https://api.deepseek.com");
        await seed.RequestAsync(c, "answer", "deepseek-flash", now.AddHours(-4), 500, 200, "https://API.deepseek.com/v1/");
        await seed.RequestAsync(c, "title", "deepseek-flash", now.AddHours(-4), 50, 10, "https://relay.example.org:8443/v1");
        await seed.RequestAsync(c, "summary", "glm-5", now.AddHours(-3), 3000, 800, "https://open.bigmodel.cn/api/paas/v4");
        await seed.RequestAsync(c, "naming", "glm-5", now.AddHours(-3), 100, 20, "https://open.bigmodel.cn/api/paas/v4");
        await seed.WorkspaceRequestAsync(h.Workspace, "deepseek-flash", now.AddHours(-2), 80, 30, "https://api.deepseek.com/v1");

        // Recommended questions and nodes (2026-11-12) count together as 推荐, apart from the names.
        await seed.WorkspaceRequestAsync(h.Workspace, "glm-5", now.AddHours(-1), 400, 60, "https://open.bigmodel.cn/api/paas/v4", "recommend-questions");
        await seed.WorkspaceRequestAsync(h.Workspace, "glm-5", now.AddHours(-1), 300, 40, "https://open.bigmodel.cn/api/paas/v4", "recommend-nodes");

        // Link suggestions (2026-11-16, §10.3.4) are a row of their own, 关联建议; the other rows are unchanged.
        await seed.WorkspaceRequestAsync(h.Workspace, "glm-5", now.AddHours(-1), 900, 120, "https://open.bigmodel.cn/api/paas/v4", "suggest-links");

        // An attachment summary made inside an answer task (2026-11-17, §10.4.3) is a row of its own, 附件摘要.
        await seed.RequestAsync(c, "answer", "deepseek-flash", now.AddHours(-4), 600, 90, "https://api.deepseek.com");
        await h.Database.WriteAsync((w, t) => w.Execute(t, "UPDATE ai_request SET purpose = 'resource_summary' WHERE prompt_tokens = 600;"), Ct);

        var report = (await Service(h, now).LoadAsync(null, UsagePeriod.Daily, Ct)).Usage;

        Assert.Equal(
            [
                (new ModelKey("glm-5", "open.bigmodel.cn"), new TokenCounts(4700, 1040, 5, 0)),
                (new ModelKey("deepseek-flash", "api.deepseek.com"), new TokenCounts(2180, 620, 4, 0)),
                (new ModelKey("deepseek-flash", "relay.example.org:8443"), new TokenCounts(50, 10, 1, 0)),
            ],
            report.ByModel.Select(m => (m.Key, m.Counts)));
        Assert.Equal(
            [
                (UsagePurpose.Answer, new TokenCounts(1500, 500, 2, 0)),
                (UsagePurpose.Title, new TokenCounts(50, 10, 1, 0)),
                (UsagePurpose.Summary, new TokenCounts(3000, 800, 1, 0)),
                (UsagePurpose.Naming, new TokenCounts(100, 20, 1, 0)),
                (UsagePurpose.WorkspaceName, new TokenCounts(80, 30, 1, 0)),
                (UsagePurpose.Recommendation, new TokenCounts(700, 100, 2, 0)),
                (UsagePurpose.LinkSuggestion, new TokenCounts(900, 120, 1, 0)),
                (UsagePurpose.AttachmentSummary, new TokenCounts(600, 90, 1, 0)),
            ],
            report.ByPurpose.Select(p => (p.Purpose, p.Counts)));
    }

    [Fact]
    public async Task Requests_without_reported_usage_are_counted_apart_and_never_estimated()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var now = Local(2026, 11, 12, 10);
        var c = await seed.ConversationAsync(h.Workspace, now.AddDays(-1));
        await seed.RequestAsync(c, "answer", "m", now.AddHours(-3), 1000, 100, estimated: 900);
        await seed.RequestAsync(c, "answer", "m", now.AddHours(-2), null, null, estimated: 5000);
        await seed.RequestAsync(c, "answer", "m", now.AddHours(-2), null, null, outcome: "failed");

        // Stopped and failed requests the service billed count like any other; a reply that gave only one count too.
        await seed.RequestAsync(c, "answer", "m", now.AddHours(-1), 300, 30, outcome: "stopped");
        await seed.RequestAsync(c, "answer", "m", now.AddHours(-1), 200, null, outcome: "failed");

        var report = (await Service(h, now).LoadAsync(null, UsagePeriod.Daily, Ct)).Usage;

        Assert.Equal(new TokenCounts(1500, 130, 3, 2), report.Totals);
        Assert.Equal(new TokenCounts(1500, 130, 3, 2), Assert.Single(report.ByModel).Counts);
        Assert.Equal(new TokenCounts(1500, 130, 3, 2), report.Buckets[^1].Counts);
        Assert.Equal(5, report.RequestsEver);
    }

    [Fact]
    public async Task The_current_workspace_counts_its_own_requests_including_its_suggested_names()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var now = Local(2026, 11, 12, 10);
        var other = await seed.WorkspaceAsync("摄影史", now.AddDays(-20));
        var mine = await seed.ConversationAsync(h.Workspace, now.AddDays(-2));
        var theirs = await seed.ConversationAsync(other, now.AddDays(-2));
        await seed.RequestAsync(mine, "answer", "m", now.AddHours(-3), 100, 10);
        await seed.WorkspaceRequestAsync(h.Workspace, "m", now.AddHours(-3), 7, 3);
        await seed.RequestAsync(theirs, "answer", "m", now.AddHours(-3), 9000, 900);
        await seed.WorkspaceRequestAsync(other, "m", now.AddHours(-3), 70, 30);
        await seed.RequestAsync(theirs, "answer", "m", now.AddYears(-2), 1, 1);

        var service = Service(h, now);
        var current = (await service.LoadAsync(h.Workspace, UsagePeriod.Daily, Ct)).Usage;
        var all = (await service.LoadAsync(null, UsagePeriod.Daily, Ct)).Usage;
        var theirsOnly = (await service.LoadAsync(other, UsagePeriod.Daily, Ct)).Usage;

        Assert.Equal(new TokenCounts(107, 13, 2, 0), current.Totals);
        Assert.Equal(new TokenCounts(9177, 943, 4, 0), all.Totals);
        Assert.Equal(new TokenCounts(9070, 930, 2, 0), theirsOnly.Totals);
        Assert.Equal((2, 5, 3), (current.RequestsEver, all.RequestsEver, theirsOnly.RequestsEver));
    }

    [Fact]
    public async Task Activity_counts_what_is_there_in_the_scope()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var now = Local(2026, 11, 12, 10);
        var other = await seed.WorkspaceAsync("摄影史", now.AddDays(-20));

        var root = await seed.ConversationAsync(h.Workspace, now.AddDays(-5));
        await seed.QuestionAsync(root, now.AddDays(-5));
        await seed.QuestionAsync(root, now.AddDays(-5).AddMinutes(3));
        var branch = await seed.ConversationAsync(h.Workspace, now.AddDays(-4), root);
        await seed.QuestionAsync(branch, now.AddDays(-4));
        var second = await seed.ConversationAsync(h.Workspace, now.AddDays(-4), branch);
        await seed.QuestionAsync(second, now.AddDays(-4));
        var lone = await seed.ConversationAsync(h.Workspace, now.AddDays(-1));
        await seed.QuestionAsync(lone, now.AddDays(-1));
        await seed.SummaryAsync(root, now.AddDays(-3), versions: 3, read: true);
        await seed.SummaryAsync(branch, now.AddDays(-3), versions: 1, read: false);
        await seed.ArchiveAsync(lone, now.AddDays(-1));

        var elsewhere = await seed.ConversationAsync(other, now.AddDays(-2));
        await seed.QuestionAsync(elsewhere, now.AddDays(-2));
        await seed.SummaryAsync(elsewhere, now.AddDays(-2), versions: 2, read: true);

        var service = Service(h, now);
        var current = (await service.LoadAsync(h.Workspace, UsagePeriod.Daily, Ct)).Activity;
        var all = (await service.LoadAsync(null, UsagePeriod.Daily, Ct)).Activity;
        var empty = (await service.LoadAsync(await seed.WorkspaceAsync("空", now), UsagePeriod.Daily, Ct)).Activity;

        Assert.Equal((5, 2, 2, 2, 4, 1, 1), (current.Questions, current.Conversations, current.Branches, current.Summaries, current.SummaryVersions, current.ReadSummaries, current.ArchivedConversations));
        Assert.Equal((6, 3, 2, 3, 6, 2, 1), (all.Questions, all.Conversations, all.Branches, all.Summaries, all.SummaryVersions, all.ReadSummaries, all.ArchivedConversations));
        Assert.Equal((0, 0, 0, 0, 0, null), (empty.Questions, empty.Conversations, empty.ActiveDays, empty.RecentActiveDays, empty.Milestones.Count, empty.FirstActiveDay));
    }

    [Fact]
    public async Task Active_days_are_distinct_local_days_with_a_question()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var now = Local(2026, 11, 12, 10);
        var c = await seed.ConversationAsync(h.Workspace, Local(2026, 3, 1, 9));

        // 22:30 UTC on 28 and 29 March are local days 28 and 30 (the spring change): two days, not one or three.
        await seed.QuestionAsync(c, new DateTimeOffset(2026, 3, 28, 22, 30, 0, TimeSpan.Zero));
        await seed.QuestionAsync(c, new DateTimeOffset(2026, 3, 29, 22, 30, 0, TimeSpan.Zero));
        await seed.QuestionAsync(c, Local(2026, 3, 30, 18));

        // Inside the last 30 days (from 14 October): three days, two questions on one of them; 13 October is outside.
        await seed.QuestionAsync(c, Local(2026, 10, 13, 23, 59));
        await seed.QuestionAsync(c, Local(2026, 10, 14, 0, 1));
        await seed.QuestionAsync(c, Local(2026, 11, 1, 9));
        await seed.QuestionAsync(c, Local(2026, 11, 1, 21));
        await seed.QuestionAsync(c, Local(2026, 11, 12, 8));

        var activity = (await Service(h, now).LoadAsync(null, UsagePeriod.Daily, Ct)).Activity;

        Assert.Equal(6, activity.ActiveDays);
        Assert.Equal(3, activity.RecentActiveDays);
        Assert.Equal(new DateOnly(2026, 3, 28), activity.FirstActiveDay);
    }

    [Fact]
    public async Task Milestones_are_the_local_dates_thresholds_were_reached()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var now = Local(2026, 11, 12, 10);
        var c = await seed.ConversationAsync(h.Workspace, Local(2026, 10, 1, 9));

        // The first question at 23:30 UTC on 20 October is already 21 October locally.
        await seed.QuestionAsync(c, new DateTimeOffset(2026, 10, 20, 22, 30, 0, TimeSpan.Zero));
        for (var i = 1; i < 12; i++)
        {
            await seed.QuestionAsync(c, Local(2026, 10, 21, 10).AddDays(i));
        }

        await seed.SummaryAsync(c, Local(2026, 11, 2, 15));

        var milestones = (await Service(h, now).LoadAsync(null, UsagePeriod.Daily, Ct)).Activity.Milestones;

        Assert.Equal(
            [
                new Milestone(MilestoneKind.Question, 1, new DateOnly(2026, 10, 21)),
                new Milestone(MilestoneKind.Question, 10, new DateOnly(2026, 10, 30)),
                new Milestone(MilestoneKind.Summary, 1, new DateOnly(2026, 11, 2)),
            ],
            milestones);
    }

    [Fact]
    public async Task Usage_of_deleted_conversations_and_workspaces_is_no_longer_counted()
    {
        // Through the real services: answers and titles record their requests; deleting takes the records with them.
        await using var h = await ChatHarness.StartAsync();
        h.Client.PromptTokens = _ => 120;
        h.Client.CompletionTokens = _ => 30;
        var kept = await h.Conversations.StartAsync(h.Workspace, "留下的问题", Ct);
        var deleted = await h.Conversations.StartAsync(h.Workspace, "要删除的问题", Ct);
        await h.SettleAsync(kept.Conversation);
        await h.SettleAsync(deleted.Conversation);
        var other = await h.WorkspaceService.CreateAsync(WorkspaceName.FromStored("另一个"), Ct);
        await new UsageSeed(h.Database).WorkspaceRequestAsync(other.Id, "fake-model", DateTimeOffset.UtcNow, 40, 10);

        var service = new UsageStatisticsService(new SqliteStatisticsStore(h.Database), h.Settings, TimeProvider.System, new UsageStatisticsOptions());
        var before = await service.LoadAsync(null, UsagePeriod.Daily, Ct);
        Assert.Equal(new TokenCounts(4 * 120 + 40, 4 * 30 + 10, 5, 0), before.Usage.Totals);
        Assert.Equal(2, before.Activity.Questions);
        Assert.True(before.HasModel);
        Assert.Equal(new ModelKey("fake-model", "fake.example"), before.Usage.ByModel[0].Key);

        await h.DeleteAsync(deleted.Conversation);
        Assert.Equal(WorkspaceDeleteStatus.Deleted, (await h.WorkspaceDeletion.DeleteAsync(other.Id, Ct)).Status);

        var after = await service.LoadAsync(null, UsagePeriod.Daily, Ct);
        Assert.Equal(new TokenCounts(2 * 120, 2 * 30, 2, 0), after.Usage.Totals);
        Assert.Equal((1, 1), (after.Activity.Questions, after.Activity.Conversations));
        Assert.Equal(2, after.Usage.RequestsEver);
    }

    [Fact]
    public async Task The_usage_calendar_has_every_local_day_of_the_last_52_weeks_whichever_the_period()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var seed = new UsageSeed(h.Database);
        var c = await seed.ConversationAsync(h.Workspace, Local(2025, 11, 1, 9));

        // The calendar starts on Monday 2025-11-17 (52 weeks before the week of Tuesday 2026-11-17): the Sunday before
        // is outside it, the first Monday inside, and both are outside the 30-day and 12-month windows.
        await seed.RequestAsync(c, "answer", "m", Local(2025, 11, 16, 23, 59), 1, 1);
        await seed.RequestAsync(c, "answer", "m", Local(2025, 11, 17, 0, 0), 300, 30);
        await seed.RequestAsync(c, "answer", "m", Local(2026, 11, 17, 8), 50, 5);

        foreach (var period in new[] { UsagePeriod.Daily, UsagePeriod.Monthly })
        {
            var report = await Service(h, Local(2026, 11, 17, 12)).LoadAsync(null, period, Ct);
            Assert.Equal(366, report.Days.Count);
            Assert.Equal((new DateOnly(2025, 11, 17), new DateOnly(2026, 11, 17)), (report.Days[0].Start, report.Days[^1].Start));
            Assert.Equal(new TokenCounts(300, 30, 1, 0), report.Days[0].Counts);
            Assert.Equal(new TokenCounts(50, 5, 1, 0), report.Days[^1].Counts);
            Assert.Equal(364, report.Days.Count(d => d.Counts == default));

            // The period's own figures are unchanged by the wider read.
            Assert.Equal(new TokenCounts(50, 5, 1, 0), report.Usage.Totals);
            Assert.Equal(3, report.Usage.RequestsEver);
        }
    }

    [Fact]
    public async Task Nothing_yet_is_an_empty_report_with_every_bucket()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var report = await Service(h, Local(2026, 11, 12, 10)).LoadAsync(h.Workspace, UsagePeriod.Monthly, Ct);

        Assert.False(report.HasModel);
        Assert.Equal(new DateOnly(2026, 11, 12), report.Today);
        Assert.Equal(12, report.Usage.Buckets.Count);
        Assert.All(report.Usage.Buckets, b => Assert.Equal(default, b.Counts));
        Assert.Empty(report.Usage.ByModel);
        Assert.All(report.Usage.ByPurpose, p => Assert.Equal(default, p.Counts));
        Assert.Equal(0, report.Usage.RequestsEver);
        Assert.Null(report.Activity.FirstActiveDay);
    }
}
