using SupportRag.Core.Ingest;
using SupportRag.Core.Pipeline;

namespace SupportRag.Tests;

public class SchedulingTests
{
    [Fact]
    public void Next_run_is_later_today_when_the_hour_has_not_passed()
    {
        var now = new DateTimeOffset(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(2), NightlyIngestService.UntilNextRun(now, 3));
    }

    [Fact]
    public void Next_run_is_tomorrow_when_the_hour_has_passed()
    {
        var now = new DateTimeOffset(2026, 10, 30, 3, 0, 1, TimeSpan.Zero);
        var delay = NightlyIngestService.UntilNextRun(now, 3);
        Assert.True(delay > TimeSpan.FromHours(23.9) && delay < TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData("Manual > Trammel > SLOW attack", "Trammel > SLOW attack")]
    [InlineData("FAQ > How do I activate a license?", "FAQ > How do I activate a license?")]
    [InlineData("Remanence", "Remanence")]
    public void Source_titles_keep_the_last_two_segments(string title, string expected) => Assert.Equal(expected, SupportAssistant.ShortTitle(title));
}
