using System;
using System.Collections.Generic;
using System.IO;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modQuery模組] 影片搜尋字幕狀態快取（#188）：內嵌/網路兩半各自更新互不覆寫之純合併（<see cref="VideoSubtitleStatusStore.MergeEmbedded"/>／
/// <see cref="VideoSubtitleStatusStore.MergeWeb"/>）與檔案往返。動機＝重搜同片不再重探（網路探測會花額度）。
/// </summary>
public class VideoSubtitleStatusStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-substatus-{Guid.NewGuid():N}.json");

    // ── 純合併：各自更新一半、保留另一半 ──

    [Fact]
    public void MergeEmbedded_PreservesExistingWeb()
    {
        var map = new Dictionary<string, VideoSubtitleStatusStore.Entry>
        {
            ["v"] = new() { Web = VideoSubtitleStatusStore.WebFound, WebSource = "opensubs" },
        };
        VideoSubtitleStatusStore.MergeEmbedded(map, "v", hasManual: true, hasAuto: false);
        Assert.True(map["v"].Manual);
        Assert.False(map["v"].Auto);
        Assert.Equal(VideoSubtitleStatusStore.WebFound, map["v"].Web); // 網路部分保留
        Assert.Equal("opensubs", map["v"].WebSource);
    }

    [Fact]
    public void MergeWeb_PreservesExistingEmbedded()
    {
        var map = new Dictionary<string, VideoSubtitleStatusStore.Entry>
        {
            ["v"] = new() { Manual = true, Auto = true },
        };
        VideoSubtitleStatusStore.MergeWeb(map, "v", found: true, source: "site");
        Assert.True(map["v"].Manual); // 內嵌部分保留
        Assert.True(map["v"].Auto);
        Assert.Equal(VideoSubtitleStatusStore.WebFound, map["v"].Web);
        Assert.Equal("site", map["v"].WebSource);
    }

    [Fact]
    public void MergeWeb_None_ClearsSource()
    {
        var map = new Dictionary<string, VideoSubtitleStatusStore.Entry>();
        VideoSubtitleStatusStore.MergeWeb(map, "v", found: false, source: "ignored");
        Assert.Equal(VideoSubtitleStatusStore.WebNone, map["v"].Web);
        Assert.Null(map["v"].WebSource); // 無結果不留來源
    }

    // ── 檔案往返 ──

    [Fact]
    public void SaveEmbedded_ThenSaveWeb_BothPersist()
    {
        var path = TempPath();
        try
        {
            var store = new VideoSubtitleStatusStore(path);
            store.SaveEmbedded("v", hasManual: true, hasAuto: false);
            store.SaveWeb("v", found: true, source: "acme");
            var e = store.Get("v");
            Assert.NotNull(e);
            Assert.True(e!.Manual);
            Assert.False(e.Auto);
            Assert.Equal(VideoSubtitleStatusStore.WebFound, e.Web); // 網路存檔未洗掉內嵌
            Assert.Equal("acme", e.WebSource);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveWeb_ThenSaveEmbedded_DoesNotClobberWeb()
    {
        var path = TempPath();
        try
        {
            var store = new VideoSubtitleStatusStore(path);
            store.SaveWeb("v", found: false, source: null);
            store.SaveEmbedded("v", hasManual: false, hasAuto: true);
            var e = store.Get("v")!;
            Assert.Equal(VideoSubtitleStatusStore.WebNone, e.Web); // 內嵌存檔未洗掉網路
            Assert.True(e.Auto);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Get_Unknown_ReturnsNull()
    {
        var path = TempPath();
        try
        {
            Assert.Null(new VideoSubtitleStatusStore(path).Get("nope"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_EmptyVideoId_Ignored()
    {
        var path = TempPath();
        try
        {
            var store = new VideoSubtitleStatusStore(path);
            store.SaveEmbedded("", true, true); // 空 id 不記
            Assert.Empty(store.Load());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_MalformedJson_ReturnsEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not valid ");
        try { Assert.Empty(new VideoSubtitleStatusStore(path).Load()); }
        finally { File.Delete(path); }
    }
}
