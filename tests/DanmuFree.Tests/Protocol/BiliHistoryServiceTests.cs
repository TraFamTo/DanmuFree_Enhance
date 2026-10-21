using DanmuFree.Core.Protocol;
using DanmuFree.Tests.Helpers;

namespace DanmuFree.Tests.Protocol;

public class BiliHistoryServiceTests
{
    [Fact]
    public async Task Selects_latest_completed_broadcast_for_target_room_and_uses_live_times()
    {
        const string json = """
        {"code":0,"data":{"replay_info":[
          {"room_id":1,"start_time":1700001000,"end_time":1700001299,"video_info":{"duration":200}},
          {"room_id":1,"start_time":1700002000,"end_time":0},
          {"room_id":2,"start_time":1700003000,"end_time":1700004000},
          {"room_id":1,"start_time":1700000000,"end_time":1700000099}
        ]}}
        """;
        var handler = new FakeHttpHandler().When("AnchorGetReplayList", json);
        var service = new BiliHistoryService(new HttpClient(handler));

        var record = await service.GetLastBroadcastAsync(1, CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal(1700001000, record.StartedAt.ToUnixTimeSeconds());
        Assert.Equal(299, (record.EndedAt - record.StartedAt).TotalSeconds);
    }

    [Theory]
    [InlineData("""{"code":-101,"message":"未登录"}""")]
    [InlineData("""{"code":0,"data":{"replay_info":null}}""")]
    [InlineData("""{"code":0,"data":{"replay_info":[]}}""")]
    [InlineData("""{"code":0,"data":{}}""")]
    [InlineData("not json")]
    [InlineData("""{"code":0,"data":{"replay_info":[{"room_id":2,"start_time":1700000000,"end_time":1700000010}]}}""")]
    [InlineData("""{"code":0,"data":{"replay_info":[{"room_id":1,"start_time":1700000000,"end_time":0}]}}""")]
    [InlineData("""{"code":0,"data":{"replay_info":[{"room_id":1,"start_time":1700000010,"end_time":1700000000}]}}""")]
    [InlineData("""{"code":0,"data":{"replay_info":[{"room_id":1,"start_time":1,"end_time":9223372036854775807}]}}""")]
    public async Task Unavailable_or_invalid_history_does_not_invent_duration(string json)
    {
        var handler = new FakeHttpHandler().When("AnchorGetReplayList", json);
        Assert.Null(await new BiliHistoryService(new HttpClient(handler)).GetLastBroadcastAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_item_does_not_hide_other_valid_records()
    {
        var handler = new FakeHttpHandler().When("AnchorGetReplayList", """
        {"code":0,"data":{"replay_info":[null,{"room_id":"oops"},
          {"room_id":1,"start_time":1700000000,"end_time":1700000010}]}}
        """);
        Assert.NotNull(await new BiliHistoryService(new HttpClient(handler)).GetLastBroadcastAsync(1, CancellationToken.None));
    }

    [Theory]
    [InlineData("0.1", "0.10")]
    [InlineData("0", "0.00")]
    [InlineData("\"12.34\"", "12.34")]
    public async Task Reads_income_for_selected_live_key_without_dividing_it_again(string amount, string expected)
    {
        var handler = new FakeHttpHandler()
            .When("AnchorGetReplayList", """{"code":0,"data":{"replay_info":[{"room_id":1,"start_time":1700000000,"end_time":1700000299,"live_key":"selected-key"}]}}""")
            .When("StopLiveData?live_key=selected-key", """{"code":0,"data":{"HamsterRmb":AMOUNT}}""".Replace("AMOUNT", amount));
        var record = await new BiliHistoryService(new HttpClient(handler)).GetLastBroadcastAsync(1, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), record.IncomeYuan);
        Assert.Equal("selected-key", record.LiveKey);
    }

    [Theory]
    [InlineData("""{"code":-101}""")]
    [InlineData("""{"code":0,"data":{}}""")]
    [InlineData("""{"code":0,"data":{"HamsterRmb":null}}""")]
    [InlineData("""{"code":0,"data":{"HamsterRmb":-1}}""")]
    [InlineData("""{"code":0,"data":{"HamsterRmb":"invalid"}}""")]
    [InlineData("not-json")]
    public async Task Income_unavailable_preserves_duration_and_is_not_zero(string json)
    {
        var handler = new FakeHttpHandler()
            .When("AnchorGetReplayList", """{"code":0,"data":{"replay_info":[{"room_id":1,"start_time":1700000000,"end_time":1700000299,"live_key":"key"}]}}""")
            .When("StopLiveData", json);
        var record = await new BiliHistoryService(new HttpClient(handler)).GetLastBroadcastAsync(1, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(299, (record.EndedAt - record.StartedAt).TotalSeconds);
        Assert.Null(record.IncomeYuan);
    }
}
