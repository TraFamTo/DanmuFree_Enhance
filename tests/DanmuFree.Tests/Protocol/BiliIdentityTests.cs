using System.Text;
using DanmuFree.Core.Models;
using DanmuFree.Core.Protocol;

namespace DanmuFree.Tests.Protocol;

public class BiliIdentityTests
{
    private static RichMessage Parse(string info, string command = "DANMU_MSG") =>
        new MessageParser().Parse(new BiliPacket(5,
            Encoding.UTF8.GetBytes($$"""{"cmd":"{{command}}","info":{{info}}} """)))!;

    [Fact]
    public void Reads_medal_colors_level_lit_state_and_room_separately_from_guard()
    {
        var message = Parse("""[[],"你好",[42,"测试用户",1],[25,"灯牌","测试主播",777,123456,"",0,234567,345678,456789,1,0,888],[],[],0,3]""");
        var identity = Assert.IsType<BiliUserIdentity>(message.Identity);
        var medal = Assert.IsType<FansMedal>(identity.Medal);
        Assert.Equal("灯牌·25", message.Medal);
        Assert.Equal("灯牌", medal.Name);
        Assert.Equal(25, medal.Level);
        Assert.Equal(777, medal.RoomId);
        Assert.Equal(123456, medal.Color);
        Assert.Equal(234567, medal.BorderColor);
        Assert.Equal(345678, medal.StartColor);
        Assert.Equal(456789, medal.EndColor);
        Assert.False(medal.IsLit);
        Assert.True(identity.IsAdmin);
        Assert.Equal("房管", identity.RoleText);
        Assert.Equal("舰长", identity.GuardText);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "总督")]
    [InlineData(2, "提督")]
    [InlineData(3, "舰长")]
    [InlineData(99, "")]
    public void Reads_current_room_guard_without_requiring_a_medal(int level, string label)
    {
        var message = Parse($$"""[[],"hi",[42,"测试用户",0],[],[],[],0,{{level}}]""");
        Assert.Equal(label, message.Identity!.GuardText);
        Assert.Equal(label.Length > 0, message.Identity.HasGuard);
        Assert.False(message.Identity.HasMedal);
    }

    [Fact]
    public void Does_not_infer_current_room_guard_from_another_rooms_medal()
    {
        var message = Parse("""[[],"hi",[42,"测试用户"],[25,"他人灯牌","他人主播",888,123,"",0,123,123,123,1,1,999],[],[],0,0]""");
        Assert.Equal("", message.Identity!.GuardText);
        Assert.True(message.Identity.HasMedal);
    }

    [Theory]
    [InlineData("[[],\"hi\",[42,\"测试用户\"]]")]
    [InlineData("[[],\"hi\",[42,\"测试用户\"],null]")]
    [InlineData("[[],\"hi\",[42,\"测试用户\"],\"bad\",null,null,null,{}]")]
    [InlineData("[[],\"hi\",[42,\"测试用户\",{}],[{},\"灯牌\"],null,null,null,99]")]
    public void Malformed_optional_identity_does_not_drop_the_danmu(string info)
    {
        var message = Parse(info);
        Assert.NotNull(message);
        Assert.Equal("hi", message.Text);
        Assert.Equal(42, message.UserId);
        Assert.False(message.Identity!.HasMedal);
        Assert.False(message.Identity.IsAdmin);
        Assert.False(message.Identity.HasGuard);
    }

    [Fact]
    public void Handles_numeric_strings_and_cmd_suffix_and_rejects_invalid_colors()
    {
        var message = Parse("""[[],"hi",["42","测试用户","1"],["12","灯牌","主播","777",-1,"",0,999999999,"123456","456789"],[],[],0,"2"]""", "DANMU_MSG:4:0:2:2:2:0");
        Assert.Equal("提督", message.Identity!.GuardText);
        Assert.True(message.Identity.IsAdmin);
        Assert.Equal(12, message.Identity.Medal!.Level);
        Assert.Null(message.Identity.Medal.Color);
        Assert.Null(message.Identity.Medal.BorderColor);
        Assert.True(message.Identity.Medal.IsLit);
    }

    [Theory]
    [InlineData(42L, 42L, true)]
    [InlineData(42L, 99L, false)]
    [InlineData(0L, 0L, false)]
    [InlineData(null, 42L, false)]
    public void Anchor_is_based_on_resolved_owner_uid_not_login_or_nickname(long? userId, long ownerUid, bool expected)
    {
        var identity = new BiliUserIdentity(GuardLevel: 3, IsAdmin: true).ForRoom(userId, ownerUid);
        Assert.Equal(expected, identity.IsAnchor);
        Assert.Equal(expected ? "主播" : "房管", identity.RoleText);
        Assert.Equal("舰长", identity.GuardText);
    }
}
