namespace DanmuFree.Core.Protocol;

public sealed record RoomStats(int Online, int Watched, int Likes,
    int? LiveStatus = null, DateTimeOffset? LiveStartedAt = null,
    int? RoomId = null, DateTimeOffset? ObservedAt = null, BiliBroadcastRecord? LastBroadcast = null);
