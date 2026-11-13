using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Core.Tests;

public class SummaryRulesTests
{
    [Theory]
    [InlineData("标题：蒸汽机的热效率\n\n正文第一段。", "蒸汽机的热效率", "正文第一段。")]
    [InlineData("# 蒸汽机\n正文", "蒸汽机", "正文")]
    [InlineData("\n\nTitle: Steam engines\nBody", "Steam engines", "Body")]
    [InlineData("**《火箭号》**\n\n- 要点", "火箭号", "- 要点")]
    public void Generated_output_splits_into_title_and_body(string output, string title, string body)
    {
        var parsed = SummaryRules.ParseGenerated(output);

        Assert.Equal(new GeneratedSummary(title, null, body), parsed);
    }

    [Fact]
    public void A_short_title_line_after_the_title_is_the_network_label()
    {
        var parsed = SummaryRules.ParseGenerated("标题：早期蒸汽机车在铁路运输中的作用与局限\n短名：蒸汽机车\n\n正文");

        Assert.Equal(new GeneratedSummary("早期蒸汽机车在铁路运输中的作用与局限", "蒸汽机车", "正文"), parsed);
    }

    [Fact]
    public void Without_a_short_title_the_label_is_the_start_of_the_title()
    {
        var node = SummaryNodeId.New();
        var version = new SummaryVersion(SummaryVersionId.New(), node, 1, "早期蒸汽机车在铁路运输中的作用", "正文", 1, SummaryOrigin.Generated, null, null, null, DateTimeOffset.UnixEpoch);

        Assert.Equal("早期蒸汽机车在铁…", SummaryRules.ShortTitleOf(version));
        Assert.Equal("蒸汽机车", SummaryRules.ShortTitleOf(version with { ShortTitle = "蒸汽机车" }));
        Assert.Equal("蒸汽机", SummaryRules.ShortTitleOf(version with { Title = "蒸汽机" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("标题：只有标题")]
    [InlineData("标题：\n正文")]
    public void Output_without_both_title_and_body_is_rejected(string output) =>
        Assert.Null(SummaryRules.ParseGenerated(output));

    [Theory]
    [InlineData("蒸汽机", "  蒸汽机。 ")]
    [InlineData("Steam Engine", "steam   engine")]
    [InlineData("ＡＢＣ　原理", "abc 原理")]
    [InlineData("「蒸汽机」", "蒸汽机")]
    public void Names_that_differ_only_in_width_case_space_or_surrounding_punctuation_are_the_same(string a, string b) =>
        Assert.Equal(SummaryRules.NormalizeName(a), SummaryRules.NormalizeName(b));

    [Fact]
    public void Synonyms_are_not_merged() =>
        Assert.NotEqual(SummaryRules.NormalizeName("蒸汽机"), SummaryRules.NormalizeName("蒸汽发动机"));

    [Fact]
    public void Deleting_the_current_version_falls_back_to_the_nearest_earlier_kept_version()
    {
        var v = Versions(5);

        Assert.Equal(v[1].Id, SummaryRules.NextCurrent(v, v[3].Id, new HashSet<SummaryVersionId> { v[3].Id, v[2].Id }));
    }

    [Fact]
    public void Without_an_earlier_kept_version_the_newest_kept_one_becomes_current()
    {
        var v = Versions(3);

        Assert.Equal(v[2].Id, SummaryRules.NextCurrent(v, v[0].Id, new HashSet<SummaryVersionId> { v[0].Id }));
    }

    [Fact]
    public void Deleting_every_saved_version_leaves_none()
    {
        var v = Versions(2);

        Assert.Null(SummaryRules.NextCurrent(v, v[1].Id, v.Select(x => x.Id).ToHashSet()));
    }

    [Fact]
    public void Deleting_other_versions_keeps_the_current_one()
    {
        var v = Versions(3);

        Assert.Equal(v[2].Id, SummaryRules.NextCurrent(v, v[2].Id, new HashSet<SummaryVersionId> { v[0].Id }));
    }

    [Fact]
    public void Markers_are_independent()
    {
        var v = Versions(2);
        var node = Node(v[0].Id, pending: v[1].Id);

        var markers = SummaryRules.Markers(node, v[0], new SummaryReadState(v[0].Id, ReadSource.Manual, 0, null), lastFinishedSeq: 9);

        Assert.Equal(new SummaryMarkers(Unread: false, HasCandidate: true, HasLaterContent: true), markers);
        Assert.Equal(new SummaryMarkers(true, false, false), SummaryRules.Markers(Node(v[0].Id), v[0], null, lastFinishedSeq: 3));
    }

    private static SummaryNode Node(SummaryVersionId current, SummaryVersionId? pending = null) =>
        new(SummaryNodeId.New(), WorkspaceId.New(), ConversationId.New(), current, pending, "蒸汽机", "蒸汽机", NameSource.Base, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static List<SummaryVersion> Versions(int count)
    {
        var node = SummaryNodeId.New();
        return Enumerable.Range(1, count)
            .Select(i => new SummaryVersion(SummaryVersionId.New(), node, i, $"第{i}版", "正文", 3, SummaryOrigin.Generated, null, null, null, DateTimeOffset.UnixEpoch))
            .ToList();
    }

    [Theory]
    [InlineData("标题：《三体》读后感\n短名：《三体》\n\n正文", "《三体》读后感", "三体")]
    [InlineData("# **“蒸汽机车的局限”**\n短名：“蒸汽机车”\n\n正文", "蒸汽机车的局限", "蒸汽机车")]
    [InlineData("标题：“火箭号”与“机车”\n\n正文", "“火箭号”与“机车”", null)]
    public void Book_marks_and_quotes_inside_a_generated_title_are_kept(string output, string title, string? shortTitle)
    {
        var parsed = SummaryRules.ParseGenerated(output)!;

        Assert.Equal(title, parsed.Title);
        Assert.Equal(shortTitle, parsed.ShortTitle);
    }

    /// <summary>Low finding gap-summary-10: a long title ending in emoji is cut between characters, not inside one.</summary>
    [Fact]
    public void A_generated_title_is_cut_between_whole_characters()
    {
        var parsed = SummaryRules.ParseGenerated("标题：" + new string('火', 39) + "🚂🚂\n\n正文")!;

        Assert.Equal(new string('火', 39) + "🚂", parsed.Title);
    }
}
