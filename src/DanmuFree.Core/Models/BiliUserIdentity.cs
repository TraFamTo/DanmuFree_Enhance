namespace DanmuFree.Core.Models;

public sealed record FansMedal(string Name, int Level, int? Color = null,
    int? BorderColor = null, int? StartColor = null, int? EndColor = null,
    long? RoomId = null, bool IsLit = true);

/// <summary>Message-scoped identity. GuardLevel belongs to the current room, not the worn medal.</summary>
public sealed record BiliUserIdentity(FansMedal? Medal = null, int GuardLevel = 0,
    bool IsAdmin = false, bool IsAnchor = false)
{
    public string GuardText => GuardLevel switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "" };
    public string RoleText => IsAnchor ? "主播" : IsAdmin ? "房管" : "";
    public bool HasGuard => GuardText.Length > 0;
    public bool HasRole => RoleText.Length > 0;
    public bool HasMedal => Medal is not null;

    public BiliUserIdentity ForRoom(long? userId, long anchorUid) =>
        this with { IsAnchor = anchorUid > 0 && userId is > 0 && userId == anchorUid };
}
