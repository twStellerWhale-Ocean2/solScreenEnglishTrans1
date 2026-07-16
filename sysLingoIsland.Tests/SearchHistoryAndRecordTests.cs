using System;
using System.Collections.Generic;
using System.Linq;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modQuery模組] 搜尋關鍵字歷史（SearchHistoryStore.Prepend）與搜尋成果初次時間紀錄（SearchResultRecordStore.Merge）之純函式（#186）。
/// </summary>
public class SearchHistoryAndRecordTests
{
    // ── SearchHistoryStore.Prepend ──

    [Fact]
    public void Prepend_PutsNewest_First_DedupCaseInsensitive()
    {
        var r = SearchHistoryStore.Prepend(new[] { "cats", "dogs" }, "DOGS", 50);
        Assert.Equal(new[] { "DOGS", "cats" }, r); // 置頂、移除既有同值（不分大小寫）
    }

    [Fact]
    public void Prepend_NewQuery_Prepends()
    {
        Assert.Equal(new[] { "birds", "cats", "dogs" },
            SearchHistoryStore.Prepend(new[] { "cats", "dogs" }, "birds", 50));
    }

    [Fact]
    public void Prepend_CapsOldest()
    {
        var r = SearchHistoryStore.Prepend(new[] { "b", "c", "d" }, "a", 2);
        Assert.Equal(new[] { "a", "b" }, r); // 截汰最舊
    }

    [Fact]
    public void Prepend_BlankQuery_NotAdded()
    {
        Assert.Equal(new[] { "x" }, SearchHistoryStore.Prepend(new[] { "x" }, "   ", 50));
    }

    // ── SearchResultRecordStore.Merge ──

    [Fact]
    public void Merge_ExistingKept_NewRecorded()
    {
        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var existing = new Dictionary<string, string> { ["a"] = old.ToString("o") };
        var (merged, result, changed) = SearchResultRecordStore.Merge(existing, new[] { "a", "b" }, now);

        Assert.Equal(old, result["a"]);   // 既有沿用原初次時間
        Assert.Equal(now, result["b"]);   // 新片記為 now
        Assert.True(changed);             // 有新增
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_AllExisting_NoChange()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var existing = new Dictionary<string, string> { ["a"] = now.ToString("o") };
        var (_, result, changed) = SearchResultRecordStore.Merge(existing, new[] { "a" }, now);
        Assert.False(changed);
        Assert.Equal(now, result["a"]);
    }

    [Fact]
    public void Merge_SkipsEmptyIds()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var (merged, result, _) = SearchResultRecordStore.Merge(new(), new[] { "", "x" }, now);
        Assert.False(result.ContainsKey(""));
        Assert.Single(merged);
        Assert.True(result.ContainsKey("x"));
    }
}
