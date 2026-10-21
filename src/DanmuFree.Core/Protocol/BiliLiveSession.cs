using DanmuFree.Core.Models;

namespace DanmuFree.Core.Protocol;

/// <summary>按真实房间号保存的本地观测记录，不代表平台历史账单。</summary>
public sealed class BiliLiveSession
{
    public int? LiveStatus { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LastLiveSeenAt { get; set; }
    public DateTimeOffset? LastObservationAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? LastDurationSeconds { get; set; }
    public string LastDurationQualifier { get; set; } = "";
    public bool LastDurationFromHistory { get; set; }
    public DateTimeOffset? LastBroadcastStartedAt { get; set; }
    public decimal? LastIncomeYuan { get; set; }
    public decimal IncomeYuan { get; set; }
    public DateTimeOffset? IncomeObservedSince { get; set; }
    public HashSet<string> RevenueIds { get; set; } = new();

    public void Observe(int? status, DateTimeOffset? start, DateTimeOffset at, bool exactEnd = false)
    {
        if (status is not (0 or 1 or 2) || LastObservationAt > at) return;
        if (status == 1)
        {
            if (start > at) start = null;
            // 下播后的过期 HTTP 响应不能恢复上一场。
            if (start is not null && EndedAt is not null && start <= EndedAt) return;
            bool newSession = LiveStatus is 0 or 2 || (start is not null && StartedAt is not null && start != StartedAt);
            if (newSession)
            {
                // 没观察到上一场结束，不能把更早的已知记录冒充为“最后一次”。
                if (LiveStatus == 1)
                {
                    LastDurationSeconds = null;
                    LastDurationFromHistory = false;
                    LastBroadcastStartedAt = null;
                    LastIncomeYuan = null;
                }
                IncomeYuan = 0;
                IncomeObservedSince = at;
                RevenueIds.Clear();
                StartedAt = null;
            }
            StartedAt = start ?? StartedAt;
            LastLiveSeenAt = at;
            IncomeObservedSince ??= at;
        }
        else if (LiveStatus == 1)
        {
            RevenueIds.Clear(); // 场次结束后不再需要交易去重明细，只保留汇总。
            LastDurationFromHistory = false;
            LastIncomeYuan = null;
            LastBroadcastStartedAt = StartedAt;
            if (StartedAt is { } began && LastLiveSeenAt is { } last)
            {
                bool longGap = at - last > TimeSpan.FromSeconds(90);
                var end = exactEnd || !longGap ? at : last;
                LastDurationSeconds = Math.Max(0, (long)(end - began).TotalSeconds);
                LastDurationQualifier = exactEnd ? "" : longGap ? "至少" : "约";
            }
            else LastDurationSeconds = null;
            EndedAt = at;
        }
        LiveStatus = status;
        LastObservationAt = at;
    }

    public void ApplyHistory(BiliBroadcastRecord record)
    {
        if (record.EndedAt < record.StartedAt || LastBroadcastStartedAt > record.StartedAt) return;
        // B站后台记录可能尚未生成；较早场次不能覆盖刚结束的本地场次。
        if (LiveStatus is 0 or 2 && StartedAt > record.StartedAt) return;
        // 同一场次读取失败时保留上次成功拉取值；换到新场次不能沿用旧金额。
        LastIncomeYuan = record.IncomeYuan ?? (LastBroadcastStartedAt == record.StartedAt ? LastIncomeYuan : null);
        LastDurationSeconds = (long)(record.EndedAt - record.StartedAt).TotalSeconds;
        LastDurationQualifier = "";
        LastDurationFromHistory = true;
        LastBroadcastStartedAt = record.StartedAt;
    }

    public bool AddRevenue(BiliRevenue revenue)
    {
        if (LiveStatus is 0 or 2 || revenue.IncomeYuan <= 0
            || (revenue.PaidAt is { } paid && StartedAt is { } start && paid < start)) return false;
        if (!string.IsNullOrEmpty(revenue.Id) && !RevenueIds.Add(revenue.Id)) return false;
        IncomeYuan += revenue.IncomeYuan;
        return true;
    }

    public string DurationText(DateTimeOffset now) => LiveDuration.Format(LiveStatus, StartedAt, now);

    public string ControlIncomeText()
    {
        decimal? amount = LiveStatus == 1 ? IncomeYuan : LiveStatus == 0 ? LastIncomeYuan : null;
        return amount is { } value ? value.ToString("¥0.00", System.Globalization.CultureInfo.InvariantCulture)
            : LiveStatus == 0 ? "暂无数据" : "-";
    }

    public string ControlDurationText(DateTimeOffset now)
    {
        if (LiveStatus != 0) return DurationText(now);
        if (LastDurationSeconds is not { } seconds) return "未开播-最后一次直播时长暂无记录";
        var duration = TimeSpan.FromSeconds(seconds);
        return $"未开播-最后一次直播时长为{LastDurationQualifier}{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
