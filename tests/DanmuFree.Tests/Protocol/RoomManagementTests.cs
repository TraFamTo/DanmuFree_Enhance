using System.Net;
using System.Text;
using DanmuFree.Core.Protocol;

namespace DanmuFree.Tests.Protocol;

public class RoomManagementTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Url, string Method, string Body)> Requests { get; } = new();
        public long Owner = 42;
        public bool RejectWrite;
        public bool FullFirstBanPage;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add((url, request.Method.Method, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var json = request.Method == HttpMethod.Post && RejectWrite ? """{"code":-111,"message":"csrf校验失败"}"""
                : url.Contains("getInfoByRoom") ? """{"code":0,"data":{"room_info":{"room_id":1,"uid":OWNER}}}""".Replace("OWNER", Owner.ToString())
                : url.Contains("get_by_anchor") ? """{"code":0,"data":{"page":{"total_page":2},"data":[{"uid":7,"uname":"房管"}]}}"""
                : url.Contains("get_block_list") && url.Contains("page=1") && FullFirstBanPage ? "{\"code\":0,\"data\":[" + string.Join(",", Enumerable.Range(1, 20).Select(i => $"{{\"uid\":{i + 100},\"uname\":\"用户\",\"id\":{i + 200}}}")) + "]}"
                : url.Contains("get_block_list") ? """{"code":0,"data":[{"uid":9,"uname":"用户","id":123,"block_end_time":"明天"}]}"""
                : url.Contains("web-interface/card") ? """{"code":0,"data":{"card":{"mid":"9","name":"用户"}}}"""
                : """{"code":0,"data":{}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Other_room_cannot_be_managed_and_makes_no_write_request()
    {
        var handler = new Handler { Owner = 99 };
        using var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42; bili_jct=token; SESSDATA=x", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyOwnerAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AppointAsync(7, CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Appointment_and_ban_send_csrf_and_correct_target_fields()
    {
        var handler = new Handler();
        using var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42; bili_jct=a+b; SESSDATA=x", 1);
        await service.VerifyOwnerAsync(CancellationToken.None);
        await service.AppointAsync(7, CancellationToken.None);
        Assert.Contains("admin=7", handler.Requests[^1].Body);
        Assert.Contains("csrf=a%2Bb", handler.Requests[^1].Body);
        await service.BanAsync(9, 24, "测试 & 弹幕", CancellationToken.None);
        Assert.Contains("block_uid=9", handler.Requests[^1].Body);
        Assert.Contains("hour=24", handler.Requests[^1].Body);
        Assert.Contains("roomid=1", handler.Requests[^1].Body);
    }

    [Fact]
    public async Task Unban_resolves_record_id_instead_of_sending_uid_as_record_id()
    {
        var handler = new Handler();
        using var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42; bili_jct=token", 1);
        await service.VerifyOwnerAsync(CancellationToken.None);
        await service.UnbanAsync(9, CancellationToken.None);
        Assert.Contains("id=123", handler.Requests[^1].Body);
        Assert.DoesNotContain("id=9&", handler.Requests[^1].Body);
        Assert.Contains("del_room_block_user", handler.Requests[^1].Url);
    }

    [Fact]
    public async Task Lists_and_uid_lookup_parse_and_remain_read_only()
    {
        var handler = new Handler();
        using var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42; bili_jct=token", 1);
        await service.VerifyOwnerAsync(CancellationToken.None);
        var admins = await service.GetAdminsAsync(1, CancellationToken.None);
        Assert.Equal(7, admins.Users[0].Uid);
        Assert.True(admins.HasMore);
        var blocked = await service.GetBlockedAsync(1, CancellationToken.None);
        Assert.Equal(123, blocked.Users[0].BlockId);
        var users = await service.SearchUsersAsync("9", CancellationToken.None);
        Assert.Equal(9, users[0].Uid);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Missing_csrf_and_invalid_targets_never_send_mutation()
    {
        var handler = new Handler();
        using var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42", 1);
        await service.VerifyOwnerAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AppointAsync(7, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.BanAsync(9, 0, "", CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Unban_searches_later_pages_without_loading_all_users_into_one_cache()
    {
        var handler = new Handler { FullFirstBanPage = true };
        using var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42; bili_jct=token", 1);
        await service.VerifyOwnerAsync(CancellationToken.None);
        await service.UnbanAsync(9, CancellationToken.None);
        Assert.Contains(handler.Requests, r => r.Url.Contains("page=2"));
        Assert.Contains("id=123", handler.Requests[^1].Body);
    }

    [Fact]
    public async Task Server_rejection_is_reported_and_self_cannot_be_managed()
    {
        var handler = new Handler { RejectWrite = true };
        using var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42; bili_jct=token", 1);
        await service.VerifyOwnerAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DismissAsync(7, CancellationToken.None));
        Assert.Contains("csrf", error.Message);
        int count = handler.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BanAsync(42, 24, "", CancellationToken.None));
        Assert.Equal(count, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancelled_action_does_not_send_a_request()
    {
        var handler = new Handler();
        using var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42; bili_jct=token", 1);
        await service.VerifyOwnerAsync(CancellationToken.None);
        using var source = new CancellationTokenSource();
        source.Cancel();
        int count = handler.Requests.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AppointAsync(7, source.Token));
        Assert.Equal(count, handler.Requests.Count);
    }

    [Fact]
    public async Task Disposed_connection_cannot_manage_old_room()
    {
        var handler = new Handler();
        var service = new BiliRoomManagementService(new HttpClient(handler), "DedeUserID=42; bili_jct=token", 1);
        await service.VerifyOwnerAsync(CancellationToken.None);
        service.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DismissAsync(7, CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
    }
}
