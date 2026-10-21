using System.IO;
using System.Text.Json;
using DanmuFree.Core.Protocol;

namespace DanmuFree.App.Services;

public sealed class LiveSessionHistoryService
{
    private static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DanmuFree", "live-sessions.json");
    private readonly Dictionary<int, BiliLiveSession> _rooms;
    private readonly FileLogger _log = new();

    public LiveSessionHistoryService()
    {
        try
        {
            _rooms = File.Exists(HistoryPath)
                ? JsonSerializer.Deserialize<Dictionary<int, BiliLiveSession>>(File.ReadAllText(HistoryPath))! : new();
            _rooms ??= new();
            // 仅保留最近一次使用的房间汇总，不累计房间或场次历史。
            if (_rooms.Count > 1)
            {
                var last = _rooms.OrderByDescending(p => p.Value?.LastObservationAt).First();
                _rooms.Clear(); _rooms[last.Key] = last.Value;
            }
        }
        catch (Exception e)
        {
            _log.Error("读取直播记录失败，使用空记录", e);
            _rooms = new();
        }
    }

    // 只由 UI 线程访问，短号和长号均解析为相同的真实房间号。
    public BiliLiveSession Get(int roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var session) || session is null)
        {
            _rooms.Clear();
            _rooms[roomId] = session = new BiliLiveSession();
        }
        return session;
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var temporary = HistoryPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_rooms));
            File.Move(temporary, HistoryPath, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            _log.Error("保存直播记录失败", e);
            return false;
        }
    }
}
