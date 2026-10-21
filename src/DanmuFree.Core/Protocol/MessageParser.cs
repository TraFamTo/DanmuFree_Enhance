using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DanmuFree.Core.Models;

namespace DanmuFree.Core.Protocol;

/// <summary>
/// Parses a <see cref="BiliPacket"/> into a <see cref="RichMessage"/>. op 3 heartbeat
/// replies carry a big-endian int32 online count; op 5 packets carry a JSON object whose
/// <c>cmd</c> field selects the message kind. Any parse failure (malformed body, unknown
/// <c>cmd</c>, unexpected schema drift) yields <c>null</c> rather than throwing — B站
/// JSON field paths change over time and a single bad frame must not break the stream.
/// </summary>
public sealed class MessageParser
{
    public RichMessage? Parse(BiliPacket packet) => ParseAll(packet).FirstOrDefault();

    /// <summary>批量契约：一帧可产出多条消息（如 SEND_GIFT_V2 的 gift_list 重复字段）。</summary>
    public IReadOnlyList<RichMessage> ParseAll(BiliPacket packet)
    {
        try
        {
            return packet.Operation switch
            {
                3 => Wrap(ParseOnlineCountBody(packet.Body)),
                5 => ParseJsonAll(packet.Body),
                _ => Array.Empty<RichMessage>(),
            };
        }
        catch { return Array.Empty<RichMessage>(); } // 单帧解析失败不影响后续
    }

    static IReadOnlyList<RichMessage> Wrap(RichMessage? m) =>
        m is null ? Array.Empty<RichMessage>() : new[] { m };

    static RichMessage? ParseOnlineCountBody(ReadOnlyMemory<byte> body)
    {
        if (body.Length < 4) return null;
        int count = BinaryPrimitives.ReadInt32BigEndian(body.Span[..4]);
        return new RichMessage(MessageType.OnlineCount, "", "当前在线", count.ToString(), DateTime.Now);
    }

    static IReadOnlyList<RichMessage> ParseJsonAll(ReadOnlyMemory<byte> body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("cmd", out var cmdEl)) return Array.Empty<RichMessage>();
        var cmd = cmdEl.GetString() ?? "";
        var now = DateTime.Now;
        // INTERACT_WORD 与升级版 INTERACT_WORD_V2（直播流现发 V2）共用解析；V1=data.type，V2=data.msg_type，
        // 两者都读，且兼容带后缀的 cmd（如 INTERACT_WORD_V2@xxx）。
        if (cmd.StartsWith("INTERACT_WORD", StringComparison.Ordinal))
            return Wrap(ParseInteract(root, now));
        // SEND_GIFT_V2（2026-07 灰度的新礼物协议）：一帧可带多条礼物（gift_list 重复字段），走批量路径。
        if (cmd == "SEND_GIFT_V2")
            return ParseGiftV2(root, now);
        return Wrap(cmd switch
        {
            "DANMU_MSG" => ParseDanmu(root, now),
            "SEND_GIFT" => ParseSendGift(root, now),
            "GUARD_BUY" => ParseGuardBuy(root, now),
            "SUPER_CHAT_MESSAGE" => ParseSuperChat(root, now),
            "ONLINE_GUEST_COUNT" => ParseOnlineGuestCount(root, now),
            "ONLINE_RANK_COUNT" => ParseOnlineRankCount(root, now),
            "WATCHED_CHANGE" => ParseWatchedChange(root, now),
            "LIVE" => ParseLiveState(root, now, true),
            "PREPARING" => ParseLiveState(root, now, false),
            _ => null,
        });
    }

    static RichMessage? ParseDanmu(JsonElement root, DateTime now)
    {
        var info = root.GetProperty("info");
        // info[1] is the danmu text (plain string per live B站 protocol);
        // info[2] = [uid, username, …].
        var text = info[1].GetString() ?? "";
        var user = info[2][1].GetString() ?? "";
        // info[3] = [medal_level(int), medal_name(string), anchor_name, room_id, …];
        // absent or [0,""] when the user wears no 粉丝勋章.
        string? medal = null;
        if (info.GetArrayLength() > 3 && info[3].GetArrayLength() > 1)
        {
            var medalName = info[3][1].GetString();
            if (!string.IsNullOrEmpty(medalName))
                medal = $"{medalName}·{info[3][0].GetInt32()}";
        }
        var uidElement = info[2][0];
        long? uid = long.TryParse(uidElement.ToString(), out var parsedUid) && parsedUid > 0 ? parsedUid : null;
        return new RichMessage(MessageType.Danmu, user, text, null, now, medal, UserId: uid);
    }

    static RichMessage? ParseSendGift(JsonElement root, DateTime now)
    {
        var d = root.GetProperty("data");
        return new RichMessage(MessageType.Gift,
            d.GetProperty("uname").GetString() ?? "",
            "",
            $"{d.GetProperty("giftName").GetString()} x{d.GetProperty("num").GetInt32()}",
            now, Revenue: GiftRevenue(ReadString(d, "coin_type"), ReadLong(d, "total_coin"),
                ReadLong(d, "price"), ReadLong(d, "num"),
                GiftTransactionId(d, root),
                ReadLong(d, "giftId"), UnixTime(ReadLong(d, "timestamp"))));
    }

    // 上舰/续费（舰长/提督/总督）按礼物路由：显示进通知窗、朗读走礼物聚合器（「xx 送了 舰长」）。
    // 字段名与 SEND_GIFT 不同：username / gift_name（num=购买/续费月数，通常 1）。
    static RichMessage? ParseGuardBuy(JsonElement root, DateTime now)
    {
        var d = root.GetProperty("data");
        return new RichMessage(MessageType.Gift,
            d.GetProperty("username").GetString() ?? "",
            "",
            $"{d.GetProperty("gift_name").GetString()} x{d.GetProperty("num").GetInt32()}",
            now, Revenue: GuardRevenue(d, root));
    }

    static RichMessage? ParseSuperChat(JsonElement root, DateTime now)
    {
        var d = root.GetProperty("data");
        return new RichMessage(MessageType.SuperChat,
            d.GetProperty("user_info").GetProperty("uname").GetString() ?? "",
            d.GetProperty("message").GetString() ?? "",
            $"¥{d.GetProperty("price").GetInt32()}",
            now, Revenue: new BiliRevenue(Math.Max(0, ReadLong(d, "price") ?? 0) / 2m,
                ReadString(d, "id") is { } id ? "sc:" + id : null,
                UnixTime(ReadLong(d, "start_time"))));
    }

    // 协议中的 gold 金瓜子：100 金瓜子 = 1 电池，1000 金瓜子 = 1 元。
    // 按用户指定公式电池 / 10 / 2，等价于金瓜子 / 2000。
    static BiliRevenue? GiftRevenue(string? coin, long? total, long? price, long? count,
        string? tid, long? giftId, DateTimeOffset? paidAt)
    {
        if (coin != "gold") return null;
        decimal amount = total is >= 0 ? total.Value
            : price is >= 0 && count is > 0 ? (decimal)price.Value * count.Value : 0;
        return new BiliRevenue(amount / 2000m,
            string.IsNullOrEmpty(tid) ? null : $"gift:{tid}:{giftId}", paidAt);
    }

    static BiliRevenue? GuardRevenue(JsonElement data, JsonElement root)
    {
        var price = ReadLong(data, "price");
        var count = ReadLong(data, "num");
        if (price is not >= 0 || count is not > 0) return null;
        string? id = ReadString(root, "msg_id");
        if (id is null && ReadLong(data, "start_time") is { } start && ReadLong(data, "uid") is { } uid)
            id = $"{uid}:{ReadLong(data, "gift_id")}:{start}:{ReadLong(data, "end_time")}:{count}";
        return new BiliRevenue((decimal)price.Value * count.Value / 2000m,
            id is null ? null : "guard:" + id, UnixTime(ReadLong(data, "start_time")));
    }

    static string? GiftTransactionId(JsonElement data, JsonElement root)
    {
        if (ReadString(data, "tid") is { Length: > 0 } tid) return tid;
        // rnd 有时只是时间戳，必须带 UID，避免不同用户同秒送礼被误判为重复。
        if (ReadString(data, "rnd") is { Length: > 0 } rnd && ReadLong(data, "uid") is { } uid)
            return $"rnd:{uid}:{rnd}";
        return ReadString(root, "msg_id");
    }

    static RichMessage ParseLiveState(JsonElement root, DateTime now, bool live)
    {
        var sent = ReadLong(root, "send_time");
        var sourceTime = sent is > 0 && sent <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(sent.Value) : (DateTimeOffset?)null;
        return new RichMessage(live ? MessageType.LiveStarted : MessageType.LiveEnded, "",
            !live && ReadLong(root, "round") == 1 ? "轮播中" : "", null, now,
            SourceTime: sourceTime, BroadcastStartedAt: live ? UnixTime(ReadLong(root, "live_time")) : null);
    }

    static long? ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out n) ? n : null;
    }

    static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }

    static DateTimeOffset? UnixTime(long? seconds) => seconds is > 0
        && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
        ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    static RichMessage? ParseOnlineGuestCount(JsonElement root, DateTime now)
    {
        var d = root.GetProperty("data");
        return new RichMessage(MessageType.OnlineCount, "", "当前在线",
            d.GetProperty("online_count").GetInt32().ToString(), now);
    }

    // B站真实在线人数（2026-09 探针实测 7777）：WS 每 2~5s 推一次，data.count 即真实在线
    //（与 queryContributionRank?type=online_rank 的 data.count 同源；对照 op3 心跳恒 1、
    // getInfoByRoom 的 room_info.online=人气值 27 万——两者都不是人数）。缺失时回落 online_count 字段。
    static RichMessage? ParseOnlineRankCount(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("data", out var d)) return null;
        int count = d.TryGetProperty("count", out var c) ? c.GetInt32()
                  : d.TryGetProperty("online_count", out var oc) ? oc.GetInt32() : -1;
        if (count < 0) return null;
        return new RichMessage(MessageType.RealOnlineCount, "", "当前在线", count.ToString(), now);
    }

    // 看过累计实时推送（此前只能 60s 轮询 getInfoByRoom 的 watched_show.num）。
    static RichMessage? ParseWatchedChange(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("data", out var d)) return null;
        if (!d.TryGetProperty("num", out var n)) return null;
        return new RichMessage(MessageType.WatchedCount, "", "看过", n.GetInt32().ToString(), now);
    }

    // SEND_GIFT_V2：礼物数据编码在 data.pb（base64 protobuf，JSON 里没有 uname/giftName）。
    // 字段号跟踪 blivedm SendGiftBroadcast：顶层 f2=uname；f10=gift_list[]（重复），每项 f2=gift_name、f3=num。
    // 与 SEND_GIFT 产出同构（Gift + Extra「{名} x{N}」），显示/开关/聚合朗读全链路自动复用。
    // pb 缺失/解不出 → 空列表（不污染流）。
    static IReadOnlyList<RichMessage> ParseGiftV2(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("data", out var d)) return Array.Empty<RichMessage>();
        if (!d.TryGetProperty("pb", out var pbEl) || pbEl.ValueKind != JsonValueKind.String)
            return Array.Empty<RichMessage>();
        var b64 = pbEl.GetString();
        if (string.IsNullOrEmpty(b64)) return Array.Empty<RichMessage>();
        var (uname, gifts) = DecodeGiftV2Pb(Convert.FromBase64String(b64));
        if (uname.Length == 0 || gifts.Count == 0) return Array.Empty<RichMessage>();
        var list = new RichMessage[gifts.Count];
        for (int k = 0; k < gifts.Count; k++)
        {
            var gift = gifts[k];
            list[k] = new RichMessage(MessageType.Gift, uname, "", $"{gift.Name} x{gift.Num}", now,
                Revenue: GiftRevenue(gift.CoinType, gift.TotalCoin, gift.Price, gift.Num,
                    gift.Tid, gift.Id, UnixTime(gift.Timestamp)));
        }
        return list;
    }

    // 解码 SEND_GIFT_V2 的 data.pb：顶层只取 f2(uname, string) 与 f10(gift_item, 重复 submessage)，其余跳过。
    private sealed record GiftV2Item(string Name, int Num, long? Id, long? Price,
        long? TotalCoin, string? CoinType, string? Tid, long? Timestamp);

    static (string uname, List<GiftV2Item> gifts) DecodeGiftV2Pb(byte[] pb)
    {
        string uname = "";
        var gifts = new List<GiftV2Item>();
        int i = 0;
        while (i < pb.Length)
        {
            if (!TryReadVarint(pb, ref i, out ulong key)) break;
            int field = (int)(key >> 3), wt = (int)(key & 7);
            if (wt == 0)
            {
                if (!TryReadVarint(pb, ref i, out _)) break;
            }
            else if (wt == 2)
            {
                if (!TryReadVarint(pb, ref i, out ulong len)) break;
                int l = (int)len;
                if (l < 0 || i + l > pb.Length) break;
                if (field == 2) uname = Encoding.UTF8.GetString(pb, i, l);
                else if (field == 10) gifts.Add(DecodeGiftV2Item(pb.AsSpan(i, l)));
                i += l;
            }
            else if (wt == 5) { if (i + 4 > pb.Length) break; i += 4; }
            else if (wt == 1) { if (i + 8 > pb.Length) break; i += 8; }
            else break;
        }
        return (uname, gifts);
    }

    // gift_list 单项同时读取收入字段：f1=id / f5=单价 / f7=实付总额 / f8=币种 / f9=交易号 / f10=时间。
    static GiftV2Item DecodeGiftV2Item(ReadOnlySpan<byte> item)
    {
        string name = ""; int num = 0;
        long? id = null, price = null, total = null, timestamp = null;
        string? coin = null, tid = null;
        int i = 0;
        while (i < item.Length)
        {
            if (!TryReadVarint(item, ref i, out ulong key)) break;
            int field = (int)(key >> 3), wt = (int)(key & 7);
            if (wt == 0)
            {
                if (!TryReadVarint(item, ref i, out ulong v)) break;
                if (field == 3) num = v > int.MaxValue ? int.MaxValue : (int)v;
                if (v <= long.MaxValue)
                {
                    if (field == 1) id = (long)v;
                    if (field == 5) price = (long)v;
                    if (field == 7) total = (long)v;
                    if (field == 10) timestamp = (long)v;
                }
            }
            else if (wt == 2)
            {
                if (!TryReadVarint(item, ref i, out ulong len)) break;
                int l = (int)len;
                if (l < 0 || i + l > item.Length) break;
                if (field == 2) name = Encoding.UTF8.GetString(item.Slice(i, l));
                if (field == 8) coin = Encoding.UTF8.GetString(item.Slice(i, l));
                if (field == 9) tid = Encoding.UTF8.GetString(item.Slice(i, l));
                i += l;
            }
            else if (wt == 5) { if (i + 4 > item.Length) break; i += 4; }
            else if (wt == 1) { if (i + 8 > item.Length) break; i += 8; }
            else break;
        }
        return new GiftV2Item(name, num, id, price, total, coin, tid, timestamp);
    }

    // varint 读取（ReadOnlySpan 重载，供 gift_list 子消息解码；字节数组版本见下）。
    static bool TryReadVarint(ReadOnlySpan<byte> d, ref int i, out ulong v)
    {
        v = 0; int s = 0;
        while (i < d.Length)
        {
            byte b = d[i++];
            v |= (ulong)(b & 0x7f) << s;
            if ((b & 0x80) == 0) return true;
            s += 7;
            if (s > 63) return false;
        }
        return false;
    }

    // INTERACT_WORD（旧）/ INTERACT_WORD_V2（现直播流发的）：msg_type 1=进入直播间, 2=关注了主播, 3=分享了直播间。
    //   - V2：交互信息编码在 data.pb（base64 protobuf），顶层字段 f2=uname、f5=msg_type、f1=uid；
    //     JSON 里没有 msg_type / uname，必须解码 pb（实测 7777 等房间）。
    //   - V1：直接 data.type（或 data.msg_type）+ data.uname。
    //   - 未知 / 缺字段类型返回 null（不污染流）。
    static RichMessage? ParseInteract(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("data", out var d)) return null;
        int type;
        string uname;
        if (d.TryGetProperty("pb", out var pbEl) && pbEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(pbEl.GetString()))
        {
            (uname, type) = DecodeInteractPb(Convert.FromBase64String(pbEl.GetString()!));
        }
        else
        {
            type = d.TryGetProperty("msg_type", out var mt) ? mt.GetInt32()
                 : d.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
            uname = d.TryGetProperty("uname", out var u) ? u.GetString() ?? "" : "";
        }
        string text = type switch
        {
            1 => "进入直播间",
            2 => "关注了主播",
            3 => "分享了直播间",
            _ => "",
        };
        if (text.Length == 0) return null;
        return new RichMessage(MessageType.Interact, uname, text, null, now);
    }

    // 解码 INTERACT_WORD_V2 的 data.pb：只取顶层 f2(uname, string) 与 f5(msg_type, varint)，其余跳过。
    static (string uname, int msgType) DecodeInteractPb(byte[] pb)
    {
        string uname = "";
        int msgType = 0;
        int i = 0;
        while (i < pb.Length)
        {
            if (!TryReadVarint(pb, ref i, out ulong key)) break;
            int field = (int)(key >> 3), wt = (int)(key & 7);
            if (wt == 0)
            {
                if (!TryReadVarint(pb, ref i, out ulong v)) break;
                if (field == 5) msgType = (int)v;
            }
            else if (wt == 2)
            {
                if (!TryReadVarint(pb, ref i, out ulong len)) break;
                int l = (int)len;
                if (l < 0 || i + l > pb.Length) break;
                if (field == 2) uname = Encoding.UTF8.GetString(pb, i, l);
                i += l;
            }
            else if (wt == 5) { if (i + 4 > pb.Length) break; i += 4; }
            else if (wt == 1) { if (i + 8 > pb.Length) break; i += 8; }
            else break;
        }
        return (uname, msgType);
    }

    static bool TryReadVarint(byte[] d, ref int i, out ulong v)
    {
        v = 0; int s = 0;
        while (i < d.Length)
        {
            byte b = d[i++];
            v |= (ulong)(b & 0x7f) << s;
            if ((b & 0x80) == 0) return true;
            s += 7;
            if (s > 63) return false;
        }
        return false;
    }
}
