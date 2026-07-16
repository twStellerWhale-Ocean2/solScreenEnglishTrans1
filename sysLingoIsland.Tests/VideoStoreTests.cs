using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>影片清單純函式（<see cref="VideoStore.Upsert"/>／<see cref="VideoStore.RemoveFromList"/>，epic #145 增量4）：
/// 依 VideoId 去重、既有移至最前並更新（空標題不覆蓋）、新在前、依 id 移除。</summary>
public class VideoStoreTests
{
    [Fact]
    public void Upsert_NewVideos_InsertedAtFront()
    {
        var d = new VideosData();
        VideoStore.Upsert(d, "aaaaaaaaaaa", "A", null, null, "t1");
        VideoStore.Upsert(d, "bbbbbbbbbbb", "B", null, null, "t2");
        Assert.Equal(new[] { "bbbbbbbbbbb", "aaaaaaaaaaa" }, d.Items.Select(i => i.VideoId));
    }

    [Fact]
    public void Upsert_ExistingVideoId_MovedToFrontAndUpdated()
    {
        var d = new VideosData();
        VideoStore.Upsert(d, "aaaaaaaaaaa", "A", null, null, "t1");
        VideoStore.Upsert(d, "bbbbbbbbbbb", "B", null, null, "t2");
        VideoStore.Upsert(d, "aaaaaaaaaaa", "A2", "th", "Theme", "t3"); // 既有 a → 移至最前、更新
        Assert.Equal(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" }, d.Items.Select(i => i.VideoId));
        Assert.Single(d.Items, i => i.VideoId == "aaaaaaaaaaa"); // 不重複
        Assert.Equal("A2", d.Items[0].Title);
        Assert.Equal("Theme", d.Items[0].ThemeName);
        Assert.Equal("t3", d.Items[0].AddedAt);
    }

    [Fact]
    public void Upsert_EmptyTitle_FallsBackToVideoId()
    {
        var d = new VideosData();
        var it = VideoStore.Upsert(d, "ccccccccccc", "", null, null, "t");
        Assert.Equal("ccccccccccc", it.Title);
    }

    [Fact]
    public void Upsert_ExistingWithEmptyTitle_KeepsOldTitle()
    {
        var d = new VideosData();
        VideoStore.Upsert(d, "aaaaaaaaaaa", "Original", null, null, "t1");
        VideoStore.Upsert(d, "aaaaaaaaaaa", "", null, null, "t2"); // 空標題不覆蓋
        Assert.Equal("Original", d.Items[0].Title);
    }

    [Fact]
    public void RemoveFromList_ById()
    {
        var d = new VideosData();
        var a = VideoStore.Upsert(d, "aaaaaaaaaaa", "A", null, null, "t1");
        VideoStore.Upsert(d, "bbbbbbbbbbb", "B", null, null, "t2");
        var removed = VideoStore.RemoveFromList(d, a.Id);
        Assert.NotNull(removed);
        Assert.Single(d.Items);
        Assert.Equal("bbbbbbbbbbb", d.Items[0].VideoId);
    }

    // ---- SetTheme（內容區塊主題下拉重指派，#173） ----

    [Fact]
    public void SetTheme_AssignsThemeIdAndName()
    {
        var d = new VideosData();
        var a = VideoStore.Upsert(d, "aaaaaaaaaaa", "A", null, null, "t1");
        Assert.True(VideoStore.SetTheme(d, a.Id, "th", "Theme"));
        Assert.Equal("th", d.Items[0].ThemeId);
        Assert.Equal("Theme", d.Items[0].ThemeName);
    }

    [Fact]
    public void SetTheme_NullThemeId_ClearsToNoTheme()
    {
        var d = new VideosData();
        var a = VideoStore.Upsert(d, "aaaaaaaaaaa", "A", "th", "Theme", "t1");
        Assert.True(VideoStore.SetTheme(d, a.Id, null, null));
        Assert.Null(d.Items[0].ThemeId);
        Assert.Null(d.Items[0].ThemeName);
    }

    [Fact]
    public void SetTheme_BlankName_StoredAsNull()
    {
        var d = new VideosData();
        var a = VideoStore.Upsert(d, "aaaaaaaaaaa", "A", null, null, "t1");
        Assert.True(VideoStore.SetTheme(d, a.Id, "th", "   "));
        Assert.Null(d.Items[0].ThemeName);
    }

    [Fact]
    public void SetTheme_UnknownId_FalseNoChange()
    {
        var d = new VideosData();
        VideoStore.Upsert(d, "aaaaaaaaaaa", "A", "th", "Theme", "t1");
        Assert.False(VideoStore.SetTheme(d, "nope", "x", "X"));
        Assert.Equal("th", d.Items[0].ThemeId); // 未變
    }
}
