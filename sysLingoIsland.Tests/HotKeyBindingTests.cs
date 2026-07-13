using LingoIsland.Capture;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>喚起快捷鍵綁定之序列化／解析／往返（Issue #10）——純資料邏輯，可單元測試。</summary>
public class HotKeyBindingTests
{
    [Fact]
    public void Default_Is_AltL()
    {
        var b = HotKeyBinding.Default;
        Assert.Equal(HotKeyKind.Keyboard, b.Kind);
        Assert.Equal(HotKeyBinding.ModAlt, b.Modifiers);
        Assert.Equal(0x4Cu, b.VirtualKey);
        Assert.Equal("Alt+L", b.Serialize());
    }

    [Theory]
    [InlineData("Alt+L")]
    [InlineData("Ctrl+Shift+F")]
    [InlineData("Ctrl+Alt+Shift+Win+F5")]
    [InlineData("Ctrl+9")]
    [InlineData("Mouse:Middle")]
    [InlineData("Mouse:X1")]
    [InlineData("Mouse:X2")]
    [InlineData("Mouse:LeftRight")]
    public void Serialize_Parse_Roundtrips(string text)
    {
        Assert.True(HotKeyBinding.TryParse(text, out var b));
        Assert.Equal(text, b.Serialize());
    }

    [Fact]
    public void Parse_Keyboard_FixedModifierOrder()
    {
        // 修飾鍵順序正規化為 Ctrl+Alt+Shift+Win，不論輸入次序
        Assert.True(HotKeyBinding.TryParse("Shift+Ctrl+A", out var b));
        Assert.Equal("Ctrl+Shift+A", b.Serialize());
    }

    [Fact]
    public void Parse_Mouse_AcceptsXButtonAliases()
    {
        Assert.True(HotKeyBinding.TryParse("Mouse:XButton1", out var b));
        Assert.Equal(HotKeyKind.Mouse, b.Kind);
        Assert.Equal(MouseTrigger.XButton1, b.Mouse);
        Assert.Equal("Mouse:X1", b.Serialize()); // 正規化為 X1
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Foo+L")]
    [InlineData("Mouse:Wheel")]
    [InlineData("Ctrl+ThisIsNotAKey")]
    public void Parse_Invalid_ReturnsDefault_AndTryParseFalse(string? text)
    {
        Assert.False(HotKeyBinding.TryParse(text, out _));
        Assert.Equal(HotKeyBinding.Default, HotKeyBinding.Parse(text));
    }

    [Theory]
    [InlineData(0x41u, "A")]
    [InlineData(0x5Au, "Z")]
    [InlineData(0x30u, "0")]
    [InlineData(0x70u, "F1")]
    [InlineData(0x87u, "F24")]
    public void KeyName_KeyCode_Roundtrip(uint vk, string name)
    {
        Assert.Equal(name, HotKeyBinding.KeyName(vk));
        Assert.Equal(vk, HotKeyBinding.KeyCode(name));
    }

    [Fact]
    public void Factory_Helpers_ProduceExpectedKinds()
    {
        Assert.Equal(HotKeyKind.Keyboard, HotKeyBinding.Keyboard(HotKeyBinding.ModControl, 0x46).Kind);
        var mouse = HotKeyBinding.OfMouse(MouseTrigger.LeftRight);
        Assert.Equal(HotKeyKind.Mouse, mouse.Kind);
        Assert.Equal("Mouse:LeftRight", mouse.Serialize());
    }
}
