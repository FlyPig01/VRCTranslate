using VrcTranslate.Core.Settings;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class HotkeyDefaultsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Clearing_a_shortcut_keeps_it_cleared_instead_of_falling_back(string? stored)
    {
        // 设置里是空字符串 = 用户主动清空 = 该功能没有快捷键，读值方不得回落默认值。
        Assert.Equal(string.Empty, HotkeyDefaults.ResolvePersisted(stored, HotkeyDefaults.OtherPlayerVoice));
    }

    [Theory]
    [InlineData("F13")]
    [InlineData("Ctrl+Alt+Space")]
    public void An_unreadable_shortcut_keeps_the_shipped_default(string stored)
    {
        // 只有「值非法」才回落默认：这与 MainWindow.ReadHotkeys 的轮询读法一致。
        Assert.Equal(HotkeyDefaults.OtherPlayerVoice, HotkeyDefaults.ResolvePersisted(stored, HotkeyDefaults.OtherPlayerVoice));
    }

    [Fact]
    public void A_readable_shortcut_is_canonicalized_like_the_poller_reads_it()
    {
        Assert.Equal("CTRL+ALT+I", HotkeyDefaults.ResolvePersisted("alt + control + i", HotkeyDefaults.QuickInput));
        Assert.Equal("CTRL+F8", HotkeyDefaults.ResolvePersisted(" ctrl + f8 ", HotkeyDefaults.QuickInput));
    }

    [Fact]
    public void Every_shipped_default_is_itself_a_supported_gesture()
    {
        // 默认值是非法值时，回落路径自己会抛异常——这条断言把它挡在门外。
        Assert.Equal("CTRL+ALT+I", HotkeyDefaults.ResolvePersisted(HotkeyDefaults.QuickInput, HotkeyDefaults.QuickInput));
        Assert.Equal("F7", HotkeyDefaults.ResolvePersisted(HotkeyDefaults.OtherPlayerVoice, HotkeyDefaults.OtherPlayerVoice));
        Assert.Equal("CTRL+F8", HotkeyDefaults.ResolvePersisted(HotkeyDefaults.SelfVoice, HotkeyDefaults.SelfVoice));
    }
}
