using DanmuFree.Core.Protocol;

namespace DanmuFree.Tests.Protocol;

public class LiveDurationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_100_000);

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(8136, "02:15:36")]
    [InlineData(90061, "25:01:01")]
    public void Shows_elapsed_time_including_hours_over_24(int seconds, string expected)
    {
        Assert.Equal(expected, LiveDuration.Format(1, Now.AddSeconds(-seconds), Now));
    }

    [Theory]
    [InlineData(0, "未开播")]
    [InlineData(2, "轮播中")]
    [InlineData(null, "-")]
    [InlineData(99, "-")]
    public void Non_live_status_never_counts_from_an_old_start(int? status, string expected)
    {
        Assert.Equal(expected, LiveDuration.Format(status, Now.AddHours(-2), Now));
    }

    [Fact]
    public void Missing_or_future_start_is_unknown()
    {
        Assert.Equal("-", LiveDuration.Format(1, null, Now));
        Assert.Equal("-", LiveDuration.Format(1, Now.AddSeconds(1), Now));
    }

    [Fact]
    public void Uses_actual_start_and_ignores_time_zone_offset()
    {
        var start = Now.AddHours(-3).ToOffset(TimeSpan.FromHours(8));
        Assert.Equal("03:00:00", LiveDuration.Format(1, start, Now));
        Assert.Equal("03:00:01", LiveDuration.Format(1, start, Now.AddSeconds(1)));
    }
}
