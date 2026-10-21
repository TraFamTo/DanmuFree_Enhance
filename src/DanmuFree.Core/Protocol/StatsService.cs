using System.Net.Http;
using System.Text.Json;
namespace DanmuFree.Core.Protocol;

public sealed class StatsService
{
    private readonly HttpClient _http;
    private readonly BiliHistoryService? _history;

    public StatsService(HttpClient http, string? cookie)
    {
        _http = http;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        if (cookie is not null)
            _http.DefaultRequestHeaders.Add("Cookie", cookie);
        if (!string.IsNullOrWhiteSpace(cookie)) _history = new BiliHistoryService(_http);
    }

    public event Action<RoomStats>? Updated;

    public async Task<RoomStats?> GetAsync(string roomIdInput, CancellationToken ct)
    {
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            using var resp = await _http.GetAsync(
                $"https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id={roomIdInput}", ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var codeValue)
                || codeValue != 0 || !root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("room_info", out var room)
                || !room.TryGetProperty("online", out var online) || !online.TryGetInt32(out var onlineValue)
                || !data.TryGetProperty("watched_show", out var watched)
                || !watched.TryGetProperty("num", out var num) || !num.TryGetInt32(out var watchedValue)
                || !data.TryGetProperty("like_info_v3", out var likes)
                || !likes.TryGetProperty("total_likes", out var total) || !total.TryGetInt32(out var likesValue))
                return null;

            int? liveStatus = room.TryGetProperty("live_status", out var status)
                && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var statusValue)
                ? statusValue : null;
            DateTimeOffset? startedAt = null;
            if (liveStatus == 1 && room.TryGetProperty("live_start_time", out var start)
                && start.ValueKind == JsonValueKind.Number && start.TryGetInt64(out var seconds) && seconds > 0
                && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                startedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);

            int? roomId = room.TryGetProperty("room_id", out var rid) && rid.TryGetInt32(out var roomValue)
                ? roomValue : null;
            var lastBroadcast = liveStatus == 0 && roomId is { } targetRoom && _history is not null
                ? await _history.GetLastBroadcastAsync(targetRoom, ct) : null;
            return new RoomStats(onlineValue, watchedValue, likesValue, liveStatus, startedAt, roomId, observedAt,
                lastBroadcast);
        }
        catch { return null; }
    }

    public async Task StartAsync(string roomIdInput, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var stats = await GetAsync(roomIdInput, ct);
            if (stats is not null) Updated?.Invoke(stats);
            try { await Task.Delay(60_000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
