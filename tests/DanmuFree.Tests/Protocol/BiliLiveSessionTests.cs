using System.Text.Json;
using DanmuFree.Core.Models;
using DanmuFree.Core.Protocol;

namespace DanmuFree.Tests.Protocol;

public class BiliLiveSessionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    [Fact]
    public void Exact_end_is_remembered_and_only_control_text_contains_last_duration()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start.AddHours(1));
        session.Observe(0, null, Start.AddSeconds(8136), exactEnd: true);
        Assert.Equal("未开播", session.DurationText(Start.AddDays(1)));
        Assert.Equal("未开播-最后一次直播时长为02:15:36", session.ControlDurationText(Start.AddDays(1)));
    }

    [Fact]
    public void Offline_without_history_does_not_invent_a_duration()
    {
        var session = new BiliLiveSession();
        session.Observe(0, null, Start);
        Assert.Contains("暂无记录", session.ControlDurationText(Start));
    }

    [Fact]
    public void Polling_end_is_marked_estimated_and_restart_gap_uses_last_seen_time()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start.AddHours(1));
        session.Observe(0, null, Start.AddHours(10));
        Assert.Contains("至少01:00:00", session.ControlDurationText(Start.AddHours(10)));
    }

    [Fact]
    public void Income_is_deduplicated_persisted_and_reset_for_next_broadcast()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start.AddHours(1));
        Assert.True(session.AddRevenue(new BiliRevenue(5m, "gift:1", Start.AddHours(1))));
        Assert.False(session.AddRevenue(new BiliRevenue(5m, "gift:1", Start.AddHours(1))));
        var restored = JsonSerializer.Deserialize<BiliLiveSession>(JsonSerializer.Serialize(session))!;
        restored.Observe(1, Start, Start.AddHours(2));
        Assert.Equal(5m, restored.IncomeYuan);
        Assert.False(restored.AddRevenue(new BiliRevenue(5m, "gift:1", Start.AddHours(1))));
        restored.Observe(0, null, Start.AddHours(3), exactEnd: true);
        restored.Observe(1, Start.AddDays(1), Start.AddDays(1));
        Assert.Equal(0m, restored.IncomeYuan);
        Assert.False(restored.AddRevenue(new BiliRevenue(5m, "old", Start.AddHours(1))));
    }

    [Fact]
    public void Stale_snapshot_cannot_restart_a_finished_session()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start.AddHours(1));
        session.Observe(0, null, Start.AddHours(2), exactEnd: true);
        session.Observe(1, Start, Start.AddMinutes(90));
        Assert.Equal(0, session.LiveStatus);
    }

    [Fact]
    public void Unknown_start_can_be_completed_without_losing_received_income()
    {
        var session = new BiliLiveSession();
        session.Observe(1, null, Start);
        session.AddRevenue(new BiliRevenue(2m, "gift", Start.AddSeconds(1)));
        session.Observe(1, Start, Start.AddSeconds(2));
        Assert.Equal(2m, session.IncomeYuan);
    }

    [Fact]
    public void Missed_end_between_broadcasts_does_not_display_an_older_duration()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start);
        session.Observe(0, null, Start.AddHours(1), exactEnd: true);
        session.Observe(1, Start.AddDays(1), Start.AddDays(1));
        session.AddRevenue(new BiliRevenue(9m, "gift", Start.AddDays(1)));
        session.Observe(1, Start.AddDays(2), Start.AddDays(2));
        Assert.Null(session.LastDurationSeconds);
        Assert.Equal(0m, session.IncomeYuan);
    }

    [Fact]
    public void Known_polling_delay_is_marked_approximate()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start.AddHours(1));
        session.Observe(0, null, Start.AddHours(1).AddSeconds(30));
        Assert.Contains("约01:00:30", session.ControlDurationText(Start.AddHours(2)));
        Assert.False(session.AddRevenue(new BiliRevenue(10m, "offline", Start.AddHours(2))));
    }

    [Fact]
    public void Rooms_keep_separate_balances_after_serialization()
    {
        var rooms = new Dictionary<int, BiliLiveSession> { [1] = new(), [2] = new() };
        rooms[1].Observe(1, Start, Start);
        rooms[2].Observe(1, Start, Start);
        rooms[1].AddRevenue(new BiliRevenue(12m, "same-id", Start));
        rooms[2].AddRevenue(new BiliRevenue(3m, "same-id", Start));
        var restored = JsonSerializer.Deserialize<Dictionary<int, BiliLiveSession>>(JsonSerializer.Serialize(rooms))!;
        Assert.Equal(12m, restored[1].IncomeYuan);
        Assert.Equal(3m, restored[2].IncomeYuan);
    }

    [Fact]
    public void Official_history_fills_offline_duration_without_changing_income_or_live_state()
    {
        var session = new BiliLiveSession();
        session.Observe(0, null, Start.AddDays(1));
        session.ApplyHistory(new BiliBroadcastRecord(1, Start, Start.AddSeconds(299)));
        Assert.Equal("未开播-最后一次直播时长为00:04:59", session.ControlDurationText(Start.AddDays(1)));
        Assert.Equal("未开播", session.DurationText(Start.AddDays(1)));
        Assert.True(session.LastDurationFromHistory);
        Assert.Equal(0m, session.IncomeYuan);
    }

    [Fact]
    public void Delayed_history_cannot_replace_a_newer_local_broadcast()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start.AddDays(1), Start.AddDays(1));
        session.Observe(0, null, Start.AddDays(1).AddHours(1), exactEnd: true);
        session.ApplyHistory(new BiliBroadcastRecord(1, Start, Start.AddHours(2)));
        Assert.Contains("01:00:00", session.ControlDurationText(Start.AddDays(2)));
        Assert.False(session.LastDurationFromHistory);
    }

    [Fact]
    public void Official_history_corrects_estimated_local_end()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start.AddHours(1));
        session.Observe(0, null, Start.AddHours(1).AddSeconds(30));
        session.ApplyHistory(new BiliBroadcastRecord(1, Start, Start.AddHours(1).AddSeconds(5)));
        Assert.Equal("未开播-最后一次直播时长为01:00:05", session.ControlDurationText(Start.AddHours(2)));
    }

    [Fact]
    public void Historical_income_is_separate_from_realtime_income_and_cached_for_same_broadcast()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start);
        session.AddRevenue(new BiliRevenue(5m, "gift", Start));
        session.Observe(0, null, Start.AddSeconds(299), exactEnd: true);
        var record = new BiliBroadcastRecord(1, Start, Start.AddSeconds(299), "key", 0.10m);
        session.ApplyHistory(record);
        Assert.Equal("¥0.10", session.ControlIncomeText());
        Assert.Equal(5m, session.IncomeYuan);
        var restored = JsonSerializer.Deserialize<BiliLiveSession>(JsonSerializer.Serialize(session))!;
        restored.ApplyHistory(record with { IncomeYuan = null });
        Assert.Equal("¥0.10", restored.ControlIncomeText());
        restored.Observe(1, Start.AddDays(1), Start.AddDays(1));
        Assert.Equal("¥0.00", restored.ControlIncomeText());
        restored.Observe(0, null, Start.AddDays(1).AddHours(1), exactEnd: true);
        Assert.Equal("暂无数据", restored.ControlIncomeText());
    }

    [Fact]
    public void New_historical_broadcast_never_reuses_previous_income()
    {
        var session = new BiliLiveSession();
        session.Observe(0, null, Start.AddDays(2));
        session.ApplyHistory(new BiliBroadcastRecord(1, Start, Start.AddHours(1), "old", 10m));
        session.ApplyHistory(new BiliBroadcastRecord(1, Start.AddDays(1), Start.AddDays(1).AddHours(1), "new"));
        Assert.Equal("暂无数据", session.ControlIncomeText());
        session.ApplyHistory(new BiliBroadcastRecord(1, Start.AddDays(1), Start.AddDays(1).AddHours(1), "new", 0m));
        Assert.Equal("¥0.00", session.ControlIncomeText());
    }

    [Fact]
    public void Finished_broadcast_discards_transaction_details_but_keeps_summary()
    {
        var session = new BiliLiveSession();
        session.Observe(1, Start, Start);
        session.AddRevenue(new BiliRevenue(10m, "tx", Start));
        session.Observe(0, null, Start.AddHours(1), exactEnd: true);
        Assert.Empty(session.RevenueIds);
        Assert.Equal(10m, session.IncomeYuan);
        Assert.Equal(3600, session.LastDurationSeconds);
    }
}
