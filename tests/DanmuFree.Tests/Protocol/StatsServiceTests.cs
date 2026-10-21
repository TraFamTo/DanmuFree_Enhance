using DanmuFree.Core.Protocol;
using DanmuFree.Tests.Helpers;
namespace DanmuFree.Tests.Protocol;

public class StatsServiceTests
{
    private const string InfoJson = """
    {"code":0,"data":{"room_info":{"online":1234},"watched_show":{"num":5678},"like_info_v3":{"total_likes":9012}}}
    """;

    [Fact]
    public async Task GetAsync_parses_online_watched_likes()
    {
        var handler = new FakeHttpHandler().When("getInfoByRoom", InfoJson);
        var svc = new StatsService(new HttpClient(handler), cookie: null);
        var stats = await svc.GetAsync("1", CancellationToken.None);
        Assert.NotNull(stats);
        Assert.Equal(1234, stats!.Online);
        Assert.Equal(5678, stats.Watched);
        Assert.Equal(9012, stats.Likes);
    }

    [Fact]
    public async Task GetAsync_returns_null_on_bad_code()
    {
        var handler = new FakeHttpHandler().When("getInfoByRoom", """{"code":-400,"message":"请求错误"}""");
        var svc = new StatsService(new HttpClient(handler), null);
        Assert.Null(await svc.GetAsync("1", CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_returns_null_on_missing_fields()
    {
        var handler = new FakeHttpHandler().When("getInfoByRoom", """{"code":0,"data":{}}""");
        var svc = new StatsService(new HttpClient(handler), null);
        Assert.Null(await svc.GetAsync("1", CancellationToken.None));
    }

    [Fact]
    public async Task Ctor_attaches_cookie_header()
    {
        var handler = new FakeHttpHandler().When("getInfoByRoom", InfoJson);
        var svc = new StatsService(new HttpClient(handler), "DedeUserID=1; SESSDATA=x");
        await svc.GetAsync("1", CancellationToken.None);
        Assert.Equal("DedeUserID=1; SESSDATA=x", handler.LastCookieHeader);
    }

    [Theory]
    [InlineData("\"live_status\":1,\"live_start_time\":1700000000", 1, 1700000000L)]
    [InlineData("\"live_status\":0,\"live_start_time\":1700000000", 0, null)]
    [InlineData("\"live_status\":2,\"live_start_time\":1700000000", 2, null)]
    [InlineData("\"live_status\":1,\"live_start_time\":0", 1, null)]
    [InlineData("\"live_status\":1,\"live_start_time\":-1", 1, null)]
    [InlineData("\"live_status\":1,\"live_start_time\":9223372036854775807", 1, null)]
    [InlineData("\"live_status\":1", 1, null)]
    [InlineData("\"live_status\":1,\"live_start_time\":\"invalid\"", 1, null)]
    [InlineData("\"live_status\":null,\"live_start_time\":1700000000", null, null)]
    [InlineData("\"other\":0", null, null)]
    public async Task GetAsync_parses_optional_live_session_without_losing_stats(
        string fields, int? expectedStatus, long? expectedStart)
    {
        var json = InfoJson.Replace("\"online\":1234", "\"online\":1234," + fields);
        var handler = new FakeHttpHandler().When("getInfoByRoom", json);
        var svc = new StatsService(new HttpClient(handler), null);

        var stats = await svc.GetAsync("1", CancellationToken.None);

        Assert.NotNull(stats);
        Assert.Equal(1234, stats.Online);
        Assert.Equal(expectedStatus, stats.LiveStatus);
        Assert.Equal(expectedStart, stats.LiveStartedAt?.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Offline_room_uses_authenticated_history_and_real_room_id()
    {
        var handler = new FakeHttpHandler()
            .When("getInfoByRoom", InfoJson.Replace("\"online\":1234", "\"online\":1234,\"live_status\":0,\"room_id\":1"))
            .When("AnchorGetReplayList", """{"code":0,"data":{"replay_info":[{"room_id":1,"start_time":1700000000,"end_time":1700000299}]}}""");
        var stats = await new StatsService(new HttpClient(handler), "SESSDATA=test")
            .GetAsync("7777", CancellationToken.None);

        Assert.NotNull(stats?.LastBroadcast);
        Assert.Equal(1, stats.LastBroadcast.RoomId);
        Assert.Equal("SESSDATA=test", handler.LastCookieHeader);
    }

    [Fact]
    public async Task History_failure_preserves_main_statistics()
    {
        var handler = new FakeHttpHandler()
            .When("getInfoByRoom", InfoJson.Replace("\"online\":1234", "\"online\":1234,\"live_status\":0,\"room_id\":1"))
            .When("AnchorGetReplayList", """{"code":-101}""");
        var stats = await new StatsService(new HttpClient(handler), "SESSDATA=test")
            .GetAsync("1", CancellationToken.None);
        Assert.NotNull(stats);
        Assert.Equal(1234, stats.Online);
        Assert.Null(stats.LastBroadcast);
    }

    [Fact]
    public async Task Live_room_does_not_request_finished_broadcasts()
    {
        var handler = new FakeHttpHandler().When("getInfoByRoom",
            InfoJson.Replace("\"online\":1234", "\"online\":1234,\"live_status\":1,\"room_id\":1"));
        var stats = await new StatsService(new HttpClient(handler), "SESSDATA=test")
            .GetAsync("1", CancellationToken.None);
        Assert.NotNull(stats);
        Assert.Contains("getInfoByRoom", handler.LastRequestUri!);
    }
}
