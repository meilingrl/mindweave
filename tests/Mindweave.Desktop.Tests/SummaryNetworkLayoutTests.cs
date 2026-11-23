using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.Tests;

/// <summary>The measures the layouts and the canvas share.</summary>
public class SummaryNetworkLayoutTests
{
    [Fact]
    public void The_label_box_runs_from_the_dot_to_the_end_of_the_label_on_its_right()
    {
        // The label is drawn to the right of the dot (S7): origin x + r + 9, so the box is asymmetric.
        Assert.Equal(100 - SummaryNetworkLayout.NodeRadius, SummaryNetworkLayout.BoxLeft(100));
        Assert.Equal(100 + SummaryNetworkLayout.NodeRadius + SummaryNetworkLayout.LabelGap + (4 * SummaryNetworkLayout.LabelEm), SummaryNetworkLayout.BoxRight(100, SummaryNetworkLayout.LabelWidth("火车起源")));
    }
}
