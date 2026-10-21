using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanmuFree.Core.Protocol;

public sealed record RoomUser(long Uid, string Name, long? BlockId = null, string? Note = null)
{
    public string DisplayName => $"{Name}  (UID: {Uid})";
}
public sealed record RoomUserPage(IReadOnlyList<RoomUser> Users, int Page, bool HasMore);

/// <summary>主播本人直播间管理。实例绑定一个房间及登录快照，调用方在断开时取消并释放。</summary>
public sealed class BiliRoomManagementService : IDisposable
{
    private readonly HttpClient _http;
    private readonly int _roomId;
    private readonly long _accountUid;
    private readonly string? _csrf;
    private bool _verified;
    private string? _mixinKey;
    public BiliRoomManagementService(HttpClient http, string cookie, int roomId)
    {
        _http = http;
        _roomId = roomId;
        var values = cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.Last()[1]);
        long.TryParse(values.GetValueOrDefault("DedeUserID"), out _accountUid);
        _csrf = values.GetValueOrDefault("bili_jct");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
    }

    public async Task VerifyOwnerAsync(CancellationToken ct)
    {
        _verified = false;
        using var doc = await GetAsync($"https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id={_roomId}", ct);
        var room = Child(Child(doc.RootElement, "data"), "room_info");
        if (_accountUid <= 0 || Number(room, "uid") != _accountUid || Number(room, "room_id") != _roomId)
            throw new InvalidOperationException("当前房间不属于登录账号，请用主播本人账号登录并连接自己的房间。");
        _verified = true;
    }

    public async Task<RoomUserPage> GetAdminsAsync(int page, CancellationToken ct)
    {
        RequireOwner();
        page = Math.Max(1, page);
        using var doc = await GetAsync($"https://api.live.bilibili.com/xlive/app-ucenter/v1/roomAdmin/get_by_anchor?page={page}", ct);
        var data = Child(doc.RootElement, "data");
        var list = ParseUsers(Child(data, "data"), false);
        return new RoomUserPage(list, page, Number(Child(data, "page"), "total_page") > page);
    }

    public async Task<RoomUserPage> GetBlockedAsync(int page, CancellationToken ct)
    {
        RequireOwner();
        page = Math.Max(1, page);
        using var doc = await GetAsync($"https://api.live.bilibili.com/banned_service/v1/Silent/get_block_list?roomid={_roomId}&page={page}", ct);
        var data = Child(doc.RootElement, "data");
        var list = ParseUsers(data.ValueKind == JsonValueKind.Array ? data : Child(data, "list"), true);
        return new RoomUserPage(list, page, list.Count >= 20);
    }

    public async Task<IReadOnlyList<RoomUser>> SearchUsersAsync(string query, CancellationToken ct)
    {
        RequireOwner();
        query = query.Trim();
        if (query.Length == 0) throw new InvalidOperationException("请输入 UID 或昵称。");
        if (long.TryParse(query, out var uid) && uid > 0)
        {
            using var doc = await GetAsync($"https://api.bilibili.com/x/web-interface/card?mid={uid}", ct);
            var card = Child(Child(doc.RootElement, "data"), "card");
            if (Number(card, "mid") != uid) throw new InvalidOperationException("未找到该 UID 对应的用户。");
            return new[] { new RoomUser(uid, Text(card, "name") ?? uid.ToString()) };
        }
        if (_mixinKey is null)
        {
            using var nav = await GetAsync("https://api.bilibili.com/x/web-interface/nav", ct);
            var wbi = Child(Child(nav.RootElement, "data"), "wbi_img");
            var img = Text(wbi, "img_url"); var sub = Text(wbi, "sub_url");
            if (img is null || sub is null) throw new InvalidOperationException("昵称搜索暂不可用，请使用 UID 或从最近弹幕选择。");
            _mixinKey = WbiSign.GetMixinKey(Path.GetFileNameWithoutExtension(new Uri(img).AbsolutePath),
                Path.GetFileNameWithoutExtension(new Uri(sub).AbsolutePath));
        }
        var signed = WbiSign.Sign(new Dictionary<string, string>
        { ["search_type"] = "bili_user", ["keyword"] = query, ["page"] = "1" }, _mixinKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var result = await GetAsync("https://api.bilibili.com/x/web-interface/wbi/search/type?" + signed, ct);
        return ParseUsers(Child(Child(result.RootElement, "data"), "result"), false).Take(20).ToArray();
    }

    public Task AppointAsync(long uid, CancellationToken ct) => MutateAsync(
        "https://api.live.bilibili.com/xlive/web-ucenter/v1/roomAdmin/appoint", uid,
        new() { ["admin"] = uid.ToString(), ["admin_level"] = "1" }, ct);
    public Task DismissAsync(long uid, CancellationToken ct) => MutateAsync(
        "https://api.live.bilibili.com/xlive/app-ucenter/v1/roomAdmin/dismiss", uid,
        new() { ["uid"] = uid.ToString() }, ct);
    public Task BanAsync(long uid, int hours, string message, CancellationToken ct)
    {
        if (hours is < 1 or > 720) throw new ArgumentOutOfRangeException(nameof(hours), "禁言时长须为 1～720 小时。");
        return MutateAsync("https://api.live.bilibili.com/banned_service/v2/Silent/add_block_user", uid,
            new() { ["roomid"] = _roomId.ToString(), ["block_uid"] = uid.ToString(),
                ["hour"] = hours.ToString(), ["msg"] = message }, ct);
    }

    public async Task UnbanAsync(long uid, CancellationToken ct)
    {
        RequireOwner();
        var seen = new HashSet<long>();
        for (int page = 1; page <= 1000; page++)
        {
            var result = await GetBlockedAsync(page, ct);
            var user = result.Users.FirstOrDefault(u => u.Uid == uid);
            if (user?.BlockId is { } id)
            {
                await MutateAsync("https://api.live.bilibili.com/banned_service/v1/Silent/del_room_block_user", uid,
                    new() { ["roomid"] = _roomId.ToString(), ["id"] = id.ToString() }, ct);
                return;
            }
            if (!result.HasMore) throw new InvalidOperationException("该用户不在当前禁言名单中。");
            if (result.Users.FirstOrDefault()?.BlockId is not { } first || !seen.Add(first)) break;
        }
        throw new InvalidOperationException("禁言名单分页异常，请刷新后从名单选择用户。");
    }

    private async Task MutateAsync(string url, long uid, Dictionary<string, string> values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireOwner();
        if (uid <= 0 || uid == _accountUid) throw new InvalidOperationException("请选择有效的目标用户，不能对主播本人执行此操作。");
        if (string.IsNullOrEmpty(_csrf)) throw new InvalidOperationException("登录信息缺少校验字段，请重新扫码登录后连接。");
        values["csrf"] = values["csrf_token"] = _csrf;
        values["visit_id"] = "";
        using var form = new FormUrlEncodedContent(values);
        using var response = await _http.PostAsync(url, form, ct);
        using var doc = await ReadAsync(response, ct);
    }

    private void RequireOwner()
    {
        if (!_verified) throw new InvalidOperationException("尚未验证主播身份，请先连接自己的 B站直播间。");
    }
    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var response = await _http.GetAsync(url, ct);
        return await ReadAsync(response, ct);
    }
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var code = Number(doc.RootElement, "code");
        if (code != 0)
        {
            var message = Text(doc.RootElement, "message") ?? Text(doc.RootElement, "msg") ?? "返回数据异常";
            doc.Dispose();
            throw new InvalidOperationException(code is -101 or 65530 ? "登录已失效，请重新扫码登录后连接。"
                : code is -352 or -412 ? "B站暂时限制了该请求，可稍后重试或使用 UID。" : $"B站操作失败：{message}（{code}）");
        }
        return doc;
    }
    private static List<RoomUser> ParseUsers(JsonElement array, bool blocked)
    {
        var result = new List<RoomUser>();
        if (array.ValueKind == JsonValueKind.Null) return result;
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("B站名单格式变化，请稍后重试。");
        foreach (var item in array.EnumerateArray())
        {
            var uid = Number(item, "uid") ?? Number(item, "mid") ?? Number(item, "tuid");
            if (uid is not > 0) continue;
            var name = Text(item, "uname") ?? Text(item, "tname") ?? Text(item, "name") ?? uid.Value.ToString();
            name = WebUtility.HtmlDecode(Regex.Replace(name, "<[^>]*>", ""));
            result.Add(new RoomUser(uid.Value, name, blocked ? Number(item, "id") : null,
                Text(item, blocked ? "block_end_time" : "ctime")));
        }
        return result;
    }
    private static JsonElement Child(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var value) ? value : default;
    private static long? Number(JsonElement obj, string key) => long.TryParse(Text(obj, key), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) ? value : null;
    private static string? Text(JsonElement obj, string key)
    {
        var value = Child(obj, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }
    public void Dispose() { _verified = false; _mixinKey = null; _http.Dispose(); }
}
