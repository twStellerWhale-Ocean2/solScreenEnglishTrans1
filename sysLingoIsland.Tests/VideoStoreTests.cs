using System.IO;
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

    // ---- SortVideos（影片欄排序，#207）：四鍵、null 長度排末、穩定排序、舊檔相容 ----

    private static VideoItem V(string title, double? dur = null, string? theme = null, string vid = "") => new()
    {
        VideoId = string.IsNullOrEmpty(vid) ? title : vid,
        Title = title,
        ThemeName = theme,
        DurationSec = dur,
    };

    [Fact]
    public void SortVideos_DefaultOrUnknownKey_KeepsInsertionOrder()
    {
        var items = new[] { V("b"), V("a"), V("c") };
        Assert.Equal(new[] { "b", "a", "c" }, VideoStore.SortVideos(items, null).Select(i => i.Title));
        Assert.Equal(new[] { "b", "a", "c" }, VideoStore.SortVideos(items, "AddedNew").Select(i => i.Title));
        Assert.Equal(new[] { "b", "a", "c" }, VideoStore.SortVideos(items, "Bogus").Select(i => i.Title)); // 未知鍵退預設
    }

    [Fact]
    public void SortVideos_Title_NaturalCaseInsensitive()
    {
        var items = new[] { V("e10"), V("E2"), V("apple") }; // 自然排序：數字段依數值、大小寫不敏感
        Assert.Equal(new[] { "apple", "E2", "e10" }, VideoStore.SortVideos(items, "Title").Select(i => i.Title));
    }

    [Fact]
    public void SortVideos_Duration_ShortFirst_NullLast()
    {
        var items = new[] { V("long", 300), V("noDur"), V("short", 60) };
        Assert.Equal(new[] { "short", "long", "noDur" }, VideoStore.SortVideos(items, "Duration").Select(i => i.Title));
    }

    [Fact]
    public void SortVideos_Duration_NullsKeepRelativeOrder()
    {
        var items = new[] { V("n1"), V("mid", 120), V("n2") }; // 穩定排序：無值群維持相對序
        Assert.Equal(new[] { "mid", "n1", "n2" }, VideoStore.SortVideos(items, "Duration").Select(i => i.Title));
    }

    [Fact]
    public void SortVideos_Theme_GroupsByName_UnassignedLast_StableWithin()
    {
        var items = new[] { V("z", theme: "Zoo"), V("noTheme1"), V("a2", theme: "Apple"), V("a1", theme: "Apple"), V("noTheme2") };
        // 主題名自然排序群組（Apple→Zoo）、組內維持插入序（a2 在 a1 前）、未歸屬排末且維持相對序
        Assert.Equal(new[] { "a2", "a1", "z", "noTheme1", "noTheme2" }, VideoStore.SortVideos(items, "Theme").Select(i => i.Title));
    }

    [Fact]
    public void SortVideos_DoesNotMutateInput()
    {
        var items = new List<VideoItem> { V("b"), V("a") };
        VideoStore.SortVideos(items, "Title");
        Assert.Equal(new[] { "b", "a" }, items.Select(i => i.Title)); // 呈現層投影、原清單不動
    }

    [Fact]
    public void UpdateDuration_EstimateOnlyDoesNotOverwrite_ActualOverwrites()
    {
        var path = Path.Combine(Path.GetTempPath(), $"videos-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VideoStore(path);
            var it = store.Add("aaaaaaaaaaa", "A", null, null, DateTimeOffset.Now);
            store.UpdateDuration(it.Id, 100, estimateOnly: true);   // 無值 → 推估落
            Assert.Equal(100, store.Load().Items[0].DurationSec);
            store.UpdateDuration(it.Id, 999, estimateOnly: true);   // 已有值 → 推估不覆蓋（不退化）
            Assert.Equal(100, store.Load().Items[0].DurationSec);
            store.UpdateDuration(it.Id, 250);                        // 實測 → 一律覆蓋
            Assert.Equal(250, store.Load().Items[0].DurationSec);
            store.UpdateDuration(it.Id, 0);                          // 非正值不寫
            Assert.Equal(250, store.Load().Items[0].DurationSec);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VideosData_OldJsonWithoutNewFields_DeserializesToDefaults()
    {
        // 舊檔（無 SortKey/DurationSec）反序列化相容：缺鍵＝null、不失資料
        var json = """{"Items":[{"Id":"x","VideoId":"aaaaaaaaaaa","Title":"A","AddedAt":"t1"}]}""";
        var d = System.Text.Json.JsonSerializer.Deserialize<VideosData>(json)!;
        Assert.Null(d.SortKey);
        Assert.Single(d.Items);
        Assert.Null(d.Items[0].DurationSec);
        Assert.Equal("A", d.Items[0].Title);
    }
}
