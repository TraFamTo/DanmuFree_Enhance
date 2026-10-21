using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuFree.Core.Models;
using DanmuFree.Core.Protocol;

namespace DanmuFree.App.ViewModels;

public partial class RoomManagementViewModel : ViewModelBase
{
    private sealed class Context(BiliRoomManagementService service, CancellationTokenSource source)
    {
        public BiliRoomManagementService Service { get; } = service;
        public CancellationTokenSource Source { get; } = source;
        public CancellationToken Token { get; } = source.Token;
    }
    private Context? _context;
    public ObservableCollection<RoomUser> Admins { get; } = new();
    public ObservableCollection<RoomUser> BlockedUsers { get; } = new();
    public ObservableCollection<RoomUser> AdminSearchResults { get; } = new();
    public ObservableCollection<RoomUser> BanSearchResults { get; } = new();
    public ObservableCollection<RichMessage> RecentMessages { get; } = new();
    public int[] BanHourChoices { get; } = { 1, 24, 168, 720 };
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanManage))] private bool _isAvailable;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanManage))] private bool _isBusy;
    public bool CanManage => IsAvailable && !IsBusy;
    [ObservableProperty] private string _status = "连接自己的 B站直播间后可管理。";
    [ObservableProperty] private string _adminQuery = "";
    [ObservableProperty] private string _banQuery = "";
    [ObservableProperty] private int _banHours = 24;
    [ObservableProperty] private int _adminPage = 1;
    [ObservableProperty] private int _blockedPage = 1;
    [ObservableProperty] private bool _hasMoreAdmins;
    [ObservableProperty] private bool _hasMoreBlocked;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AdminTargetText))] private RoomUser? _adminTarget;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BanTargetText))] private RoomUser? _banTarget;
    public string AdminTargetText => AdminTarget?.DisplayName ?? "尚未选择用户";
    public string BanTargetText => BanTarget?.DisplayName ?? "尚未选择用户";
    [ObservableProperty] private RoomUser? _selectedAdmin;
    [ObservableProperty] private RoomUser? _selectedAdminSearch;
    [ObservableProperty] private RoomUser? _selectedBlocked;
    [ObservableProperty] private RoomUser? _selectedBanSearch;
    [ObservableProperty] private RichMessage? _selectedRecent;
    partial void OnSelectedAdminChanged(RoomUser? value) { if (value is not null) AdminTarget = value; }
    partial void OnSelectedAdminSearchChanged(RoomUser? value) { if (value is not null) AdminTarget = value; }
    partial void OnSelectedBlockedChanged(RoomUser? value) { if (value is not null) BanTarget = value; }
    partial void OnSelectedBanSearchChanged(RoomUser? value) { if (value is not null) BanTarget = value; }
    partial void OnSelectedRecentChanged(RichMessage? value)
    {
        if (value?.UserId is > 0) BanTarget = new RoomUser(value.UserId.Value, value.UserName);
    }

    public async Task AttachAsync(int roomId, string cookie, CancellationToken connectionToken)
    {
        Detach();
        var context = new Context(new BiliRoomManagementService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, cookie, roomId),
            CancellationTokenSource.CreateLinkedTokenSource(connectionToken));
        _context = context;
        IsBusy = true;
        Status = "正在验证主播身份并读取名单…";
        try
        {
            await context.Service.VerifyOwnerAsync(context.Token);
            if (!Current(context)) return;
            IsAvailable = true;
            await LoadAdminsAsync(context, 1);
            await LoadBlockedAsync(context, 1);
            if (Current(context)) Status = "已连接主播账号；请选择用户后操作。";
        }
        catch (Exception e)
        {
            if (Current(context)) Status = Explain(e);
        }
        finally { if (Current(context)) IsBusy = false; }
    }

    private bool Current(Context context) => ReferenceEquals(context, _context) && !context.Token.IsCancellationRequested;
    public void RecordMessage(RichMessage message)
    {
        if (_context is null || _context.Token.IsCancellationRequested || message.Type != MessageType.Danmu || message.UserId is not > 0) return;
        RecentMessages.Add(message);
        while (RecentMessages.Count > 200) RecentMessages.RemoveAt(0);
    }

    public void Detach()
    {
        var old = _context;
        _context = null;
        if (old is not null) { old.Source.Cancel(); old.Service.Dispose(); old.Source.Dispose(); }
        IsAvailable = false; IsBusy = false;
        Admins.Clear(); BlockedUsers.Clear(); AdminSearchResults.Clear(); BanSearchResults.Clear(); RecentMessages.Clear();
        SelectedAdmin = SelectedAdminSearch = SelectedBlocked = SelectedBanSearch = null;
        SelectedRecent = null; AdminTarget = BanTarget = null;
        AdminQuery = BanQuery = "";
        AdminPage = BlockedPage = 1;
        HasMoreAdmins = HasMoreBlocked = false;
        Status = "连接自己的 B站直播间后可管理。";
    }

    private async Task RunAsync(Func<Context, Task<string>> action)
    {
        var context = _context;
        if (context is null || !CanManage || !Current(context)) return;
        IsBusy = true; Status = "处理中…";
        try
        {
            var message = await action(context);
            if (Current(context)) Status = message;
        }
        catch (Exception e) { if (Current(context)) Status = Explain(e); }
        finally { if (Current(context)) IsBusy = false; }
    }

    private static string Explain(Exception e) => e is OperationCanceledException ? "请求超时，请重试。"
        : e is HttpRequestException ? "网络请求失败，请检查网络后重试。" : e.Message;

    private async Task LoadAdminsAsync(Context context, int page)
    {
        var result = await context.Service.GetAdminsAsync(page, context.Token);
        if (!Current(context)) return;
        Replace(Admins, result.Users); AdminPage = result.Page; HasMoreAdmins = result.HasMore;
    }
    private async Task LoadBlockedAsync(Context context, int page)
    {
        var result = await context.Service.GetBlockedAsync(page, context.Token);
        if (!Current(context)) return;
        Replace(BlockedUsers, result.Users); BlockedPage = result.Page; HasMoreBlocked = result.HasMore;
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    { target.Clear(); foreach (var item in values) target.Add(item); }

    private async Task<string> SearchAsync(Context context, string query, ObservableCollection<RoomUser> target)
    {
        query = query.Trim();
        if (query.Length == 0) return "请输入 UID 或昵称。";
        var local = RecentMessages.Where(m => m.UserId is > 0 &&
            (m.UserName.Contains(query, StringComparison.OrdinalIgnoreCase) || m.UserId.Value.ToString() == query))
            .Select(m => new RoomUser(m.UserId!.Value, m.UserName)).DistinctBy(u => u.Uid).Take(20).ToList();
        Replace(target, local);
        try
        {
            var remote = await context.Service.SearchUsersAsync(query, context.Token);
            if (!Current(context)) return "";
            Replace(target, local.Concat(remote).DistinctBy(u => u.Uid).Take(40));
            return $"找到 {target.Count} 个用户，请核对昵称和 UID 后选择。";
        }
        catch (Exception e) when (local.Count > 0 && e is not OperationCanceledException)
        { return $"已列出最近发言用户；在线搜索失败：{Explain(e)}"; }
    }

    [RelayCommand] private Task SearchAdminAsync() => RunAsync(async c =>
    { AdminTarget = null; return await SearchAsync(c, AdminQuery, AdminSearchResults); });
    [RelayCommand] private Task SearchBanAsync() => RunAsync(async c =>
    { BanTarget = null; return await SearchAsync(c, BanQuery, BanSearchResults); });
    [RelayCommand] private Task RefreshAdminsAsync() => RunAsync(async c => { await LoadAdminsAsync(c, 1); return "房管名单已刷新。"; });
    [RelayCommand] private Task RefreshBlockedAsync() => RunAsync(async c => { await LoadBlockedAsync(c, 1); return "禁言名单已刷新。"; });
    [RelayCommand] private Task PreviousAdminsAsync() => RunAsync(async c => { await LoadAdminsAsync(c, Math.Max(1, AdminPage - 1)); return "房管名单已刷新。"; });
    [RelayCommand] private Task NextAdminsAsync() => RunAsync(async c => { if (HasMoreAdmins) await LoadAdminsAsync(c, AdminPage + 1); return "房管名单已刷新。"; });
    [RelayCommand] private Task PreviousBlockedAsync() => RunAsync(async c => { await LoadBlockedAsync(c, Math.Max(1, BlockedPage - 1)); return "禁言名单已刷新。"; });
    [RelayCommand] private Task NextBlockedAsync() => RunAsync(async c => { if (HasMoreBlocked) await LoadBlockedAsync(c, BlockedPage + 1); return "禁言名单已刷新。"; });
    [RelayCommand] private Task AppointAsync() => RunAsync(async c =>
    {
        var user = AdminTarget ?? throw new InvalidOperationException("请先选择要任命的用户。");
        await c.Service.AppointAsync(user.Uid, c.Token);
        await LoadAdminsAsync(c, 1);
        return $"已任命 {user.DisplayName} 为房管，名单已刷新。";
    });
    [RelayCommand] private Task DismissAsync() => RunAsync(async c =>
    {
        var user = AdminTarget ?? throw new InvalidOperationException("请先选择要撤销的房管。");
        await c.Service.DismissAsync(user.Uid, c.Token);
        await LoadAdminsAsync(c, 1);
        return $"已撤销 {user.DisplayName} 的房管身份，名单已刷新。";
    });
    [RelayCommand] private Task BanAsync() => RunAsync(async c =>
    {
        var user = BanTarget ?? throw new InvalidOperationException("请先从弹幕、搜索结果或名单选择用户。");
        string message = SelectedRecent?.UserId == user.Uid ? SelectedRecent.Text : "";
        await c.Service.BanAsync(user.Uid, BanHours, message, c.Token);
        await LoadBlockedAsync(c, 1);
        return $"已禁言 {user.DisplayName} {BanHours} 小时，名单已刷新。";
    });
    [RelayCommand] private Task UnbanAsync() => RunAsync(async c =>
    {
        var user = BanTarget ?? throw new InvalidOperationException("请先选择要解禁的用户。");
        await c.Service.UnbanAsync(user.Uid, c.Token);
        await LoadBlockedAsync(c, 1);
        return $"已解除 {user.DisplayName} 的禁言，名单已刷新。";
    });
}
