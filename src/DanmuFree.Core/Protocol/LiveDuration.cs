using System.Globalization;

namespace DanmuFree.Core.Protocol;

public static class LiveDuration
{
    // 使用平台的本场开播时间；未知时不以连接时间代替。总小时数支持跨天直播。
    public static string Format(int? liveStatus, DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (liveStatus == 0) return "未开播";
        if (liveStatus == 2) return "轮播中";
        if (liveStatus != 1 || startedAt is null || startedAt > now) return "-";

        var elapsed = now - startedAt.Value;
        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }
}
