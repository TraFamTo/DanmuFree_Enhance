using System.Net.Http;
using System.Text.Json;
using System.Globalization;

namespace DanmuFree.Core.Protocol;

public sealed record BiliBroadcastRecord(int RoomId, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    string? LiveKey = null, decimal? IncomeYuan = null);

/// <summary>主播账号自己的后台回放列表；按开播/下播时间计算场次时长，不使用录像文件长度。</summary>
public sealed class BiliHistoryService
{
    private readonly HttpClient _http;
    // HttpClient 由 StatsService 提供，复用其 Cookie 和浏览器请求头。
    public BiliHistoryService(HttpClient http) => _http = http;

    public async Task<BiliBroadcastRecord?> GetLastBroadcastAsync(int roomId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync(
                "https://api.live.bilibili.com/xlive/app-blink/v1/anchorVideo/AnchorGetReplayList?page=1&page_size=30",
                timeout.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = doc.RootElement;
            if (Long(root, "code") != 0 || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("replay_info", out var list)
                || list.ValueKind != JsonValueKind.Array) return null;

            BiliBroadcastRecord? latest = null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var item in list.EnumerateArray())
            {
                // 必须匹配连接的真实房间号，绝不把登录账号的记录套到其他主播身上。
                if (Long(item, "room_id") != roomId || Long(item, "start_time") is not { } start
                    || Long(item, "end_time") is not { } end || start <= 0 || end < start || end > now)
                    continue;
                var record = new BiliBroadcastRecord(roomId, DateTimeOffset.FromUnixTimeSeconds(start),
                    DateTimeOffset.FromUnixTimeSeconds(end), Text(item, "live_key"));
                if (latest is null || record.StartedAt > latest.StartedAt) latest = record;
            }
            if (latest?.LiveKey is { Length: > 0 } key)
                latest = latest with { IncomeYuan = await GetIncomeAsync(key, timeout.Token) };
            return latest;
        }
        catch { return null; } // 记录缺失、登录失效或接口失败时，仍保留主统计与本地记录。
    }

    private async Task<decimal?> GetIncomeAsync(string liveKey, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(
                "https://api.live.bilibili.com/xlive/app-blink/v1/live/StopLiveData?live_key="
                + Uri.EscapeDataString(liveKey), ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (Long(root, "code") != 0 || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("HamsterRmb", out var amount))
                return null;
            decimal value;
            bool valid = amount.ValueKind == JsonValueKind.Number ? amount.TryGetDecimal(out value)
                : decimal.TryParse(amount.ValueKind == JsonValueKind.String ? amount.GetString() : null,
                    NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            // HamsterRmb 已是后台折算后的人民币收益，不再次除以 2，也不把缺失数据当成 0。
            return valid && value >= 0 ? value : null;
        }
        catch { return null; } // 收入读取失败不得丢掉已经拿到的场次时间。
    }

    private static string? Text(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }

    private static long? Long(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : null;
    }
}
