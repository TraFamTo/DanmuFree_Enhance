using DanmuFree.Core.Models;

namespace DanmuFree.Tests.Models;

public class RichMessageTests
{
    [Fact]
    public void Record_holds_all_fields()
    {
        var m = new RichMessage(MessageType.Danmu, "alice", "你好", null, new DateTime(2026,7,27,12,0,0));
        Assert.Equal(MessageType.Danmu, m.Type);
        Assert.Equal("alice", m.UserName);
        Assert.Equal("你好", m.Text);
        Assert.Null(m.Extra);
    }

    [Fact]
    public void MessageType_keeps_existing_values_and_adds_live_lifecycle_events()
    {
        Assert.Equal(6, (int)MessageType.WatchedCount);
        Assert.Contains(MessageType.LiveStarted, Enum.GetValues<MessageType>());
        Assert.Contains(MessageType.LiveEnded, Enum.GetValues<MessageType>());
    }
}
