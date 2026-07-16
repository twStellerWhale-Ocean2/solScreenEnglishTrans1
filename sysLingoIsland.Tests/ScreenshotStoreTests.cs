using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>截圖清單純函式（<see cref="ScreenshotStore.AddToList"/>／<see cref="ScreenshotStore.RemoveFromList"/>，epic #145 增量3）：
/// 新在前、超上限自末端（最舊）汰除並回被汰除項、依 id 移除。</summary>
public class ScreenshotStoreTests
{
    private static ScreenshotItem Item(string id) => new() { Id = id, File = id + ".png" };

    [Fact]
    public void AddToList_InsertsAtFront()
    {
        var d = new ScreenshotsData();
        ScreenshotStore.AddToList(d, Item("a"), 100);
        ScreenshotStore.AddToList(d, Item("b"), 100);
        Assert.Equal(new[] { "b", "a" }, d.Items.Select(i => i.Id));
    }

    [Fact]
    public void AddToList_UnderMax_NoEviction()
    {
        var d = new ScreenshotsData();
        var evicted = ScreenshotStore.AddToList(d, Item("a"), 3);
        Assert.Empty(evicted);
        Assert.Single(d.Items);
    }

    [Fact]
    public void AddToList_OverMax_EvictsOldestFromEnd()
    {
        var d = new ScreenshotsData();
        ScreenshotStore.AddToList(d, Item("a"), 2);            // [a]
        ScreenshotStore.AddToList(d, Item("b"), 2);            // [b,a]
        var evicted = ScreenshotStore.AddToList(d, Item("c"), 2); // insert c → [c,b,a] → trim → [c,b]，evict a
        Assert.Equal(new[] { "c", "b" }, d.Items.Select(i => i.Id));
        Assert.Single(evicted);
        Assert.Equal("a", evicted[0].Id);
    }

    [Fact]
    public void RemoveFromList_ById()
    {
        var d = new ScreenshotsData();
        ScreenshotStore.AddToList(d, Item("a"), 100);
        ScreenshotStore.AddToList(d, Item("b"), 100);
        var removed = ScreenshotStore.RemoveFromList(d, "a");
        Assert.NotNull(removed);
        Assert.Equal("a", removed!.Id);
        Assert.Equal(new[] { "b" }, d.Items.Select(i => i.Id));
    }

    [Fact]
    public void RemoveFromList_UnknownId_NullNoChange()
    {
        var d = new ScreenshotsData();
        ScreenshotStore.AddToList(d, Item("a"), 100);
        Assert.Null(ScreenshotStore.RemoveFromList(d, "zzz"));
        Assert.Single(d.Items);
    }

    // ---- SetTheme（內容區塊主題下拉重指派，#173） ----

    [Fact]
    public void SetTheme_AssignsAndClears()
    {
        var d = new ScreenshotsData();
        ScreenshotStore.AddToList(d, Item("a"), 100);
        Assert.True(ScreenshotStore.SetTheme(d, "a", "th", "Theme"));
        Assert.Equal("th", d.Items[0].ThemeId);
        Assert.Equal("Theme", d.Items[0].ThemeName);
        Assert.True(ScreenshotStore.SetTheme(d, "a", null, null)); // 改回未歸屬
        Assert.Null(d.Items[0].ThemeId);
        Assert.Null(d.Items[0].ThemeName);
    }

    [Fact]
    public void SetTheme_BlankName_StoredAsNull()
    {
        var d = new ScreenshotsData();
        ScreenshotStore.AddToList(d, Item("a"), 100);
        Assert.True(ScreenshotStore.SetTheme(d, "a", "th", "  "));
        Assert.Null(d.Items[0].ThemeName);
    }

    [Fact]
    public void SetTheme_UnknownId_FalseNoChange()
    {
        var d = new ScreenshotsData();
        ScreenshotStore.AddToList(d, Item("a"), 100);
        Assert.False(ScreenshotStore.SetTheme(d, "zzz", "x", "X"));
        Assert.Single(d.Items);
    }
}
