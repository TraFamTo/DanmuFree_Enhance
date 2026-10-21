using System.Text;
using DanmuFree.Core.Protocol;

namespace DanmuFree.Tests.Protocol;

public class BiliRevenueTests
{
    private static DanmuFree.Core.Models.RichMessage Parse(string json) =>
        new MessageParser().Parse(new BiliPacket(5, Encoding.UTF8.GetBytes(json)))!;

    [Theory]
    [InlineData("gold", 10000, 5)]
    [InlineData("silver", 10000, 0)]
    public void Gift_uses_paid_total_in_gold_coins_not_combo_total(string coinType, int total, decimal income)
    {
        var message = Parse("""{"cmd":"SEND_GIFT","data":{"uname":"bob","giftName":"礼物","giftId":1,"num":2,"price":9000,"total_coin":TOTAL,"combo_total_coin":999999,"coin_type":"COIN","tid":"tx1","timestamp":1700000001}}"""
            .Replace("TOTAL", total.ToString()).Replace("COIN", coinType));
        Assert.Equal(income, message.Revenue?.IncomeYuan ?? 0);
    }

    [Fact]
    public void Guard_and_sc_use_correct_units_and_half_share()
    {
        var guard = Parse("""{"cmd":"GUARD_BUY","data":{"username":"bob","uid":1,"gift_name":"舰长","gift_id":10003,"num":2,"price":198000,"start_time":1700000001}}""");
        var sc = Parse("""{"cmd":"SUPER_CHAT_MESSAGE","data":{"id":1,"price":30,"start_time":1700000001,"message":"加油","user_info":{"uname":"bob"}}}""");
        Assert.Equal(198m, guard.Revenue!.IncomeYuan);
        Assert.Equal(15m, sc.Revenue!.IncomeYuan);
    }

    [Fact]
    public void Missing_price_keeps_gift_visible_but_does_not_guess_income()
    {
        var m = Parse("""{"cmd":"SEND_GIFT","data":{"uname":"bob","giftName":"礼物","num":1}}""");
        Assert.Equal("礼物 x1", m.Extra);
        Assert.Null(m.Revenue);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0.05)]
    [InlineData(10000, 5)]
    public void Gold_amount_uses_decimal_and_respects_zero_paid_total(long total, decimal income)
    {
        var m = Parse("""{"cmd":"SEND_GIFT","data":{"uname":"bob","giftName":"礼物","num":2,"coin_type":"gold","price":10000,"total_coin":AMOUNT}}"""
            .Replace("AMOUNT", total.ToString()));
        Assert.Equal(income, m.Revenue!.IncomeYuan);
    }

    [Fact]
    public void Missing_total_uses_unit_price_times_quantity()
    {
        var m = Parse("""{"cmd":"SEND_GIFT","data":{"uname":"bob","giftName":"礼物","num":2,"coin_type":"gold","price":1000}}""");
        Assert.Equal(1m, m.Revenue!.IncomeYuan);
    }

    [Fact]
    public void Different_users_with_timestamp_rnd_do_not_collide()
    {
        const string json = """{"cmd":"SEND_GIFT","data":{"uid":USER,"uname":"bob","giftName":"礼物","giftId":1,"num":1,"coin_type":"gold","price":1000,"rnd":"1700000000"}}""";
        var first = Parse(json.Replace("USER", "1"));
        var second = Parse(json.Replace("USER", "2"));
        Assert.NotEqual(first.Revenue!.Id, second.Revenue!.Id);
    }

    [Fact]
    public void V2_paid_gift_matches_v1_amount_and_transaction_id()
    {
        using var item = new MemoryStream();
        static void Varint(Stream stream, ulong value)
        {
            while (value >= 128) { stream.WriteByte((byte)(value | 128)); value >>= 7; }
            stream.WriteByte((byte)value);
        }
        static void Number(Stream stream, int field, ulong value)
        {
            Varint(stream, (ulong)(field << 3)); Varint(stream, value);
        }
        static void Bytes(Stream stream, int field, byte[] bytes)
        {
            Varint(stream, (ulong)((field << 3) | 2)); Varint(stream, (ulong)bytes.Length); stream.Write(bytes);
        }
        Number(item, 1, 1);
        Bytes(item, 2, Encoding.UTF8.GetBytes("礼物"));
        Number(item, 3, 2);
        Number(item, 5, 9000);
        Number(item, 7, 10000); // 折后实付总额，不能再乘数量。
        Bytes(item, 8, Encoding.UTF8.GetBytes("gold"));
        Bytes(item, 9, Encoding.UTF8.GetBytes("tx1"));
        Number(item, 10, 1700000001);
        using var body = new MemoryStream();
        Bytes(body, 2, Encoding.UTF8.GetBytes("bob"));
        Bytes(body, 10, item.ToArray());
        var v2 = Parse("""{"cmd":"SEND_GIFT_V2","data":{"pb":"BASE64"}}"""
            .Replace("BASE64", Convert.ToBase64String(body.ToArray())));
        var v1 = Parse("""{"cmd":"SEND_GIFT","data":{"uname":"bob","giftName":"礼物","giftId":1,"num":2,"price":9000,"total_coin":10000,"coin_type":"gold","tid":"tx1","timestamp":1700000001}}""");
        Assert.Equal(v1.Revenue, v2.Revenue);
        var session = new BiliLiveSession();
        session.Observe(1, DateTimeOffset.FromUnixTimeSeconds(1700000000), DateTimeOffset.FromUnixTimeSeconds(1700000001));
        Assert.True(session.AddRevenue(v1.Revenue!));
        Assert.False(session.AddRevenue(v2.Revenue!));
        Assert.Equal(5m, session.IncomeYuan);
    }

    [Fact]
    public void Live_and_preparing_use_server_time()
    {
        var live = Parse("""{"cmd":"LIVE","live_time":1700000000,"send_time":1700000001000}""");
        var end = Parse("""{"cmd":"PREPARING","send_time":1700008136000}""");
        Assert.Equal(DanmuFree.Core.Models.MessageType.LiveStarted, live.Type);
        Assert.Equal(1700000000, live.BroadcastStartedAt?.ToUnixTimeSeconds());
        Assert.Equal(1700008136, end.SourceTime?.ToUnixTimeSeconds());
    }
}
