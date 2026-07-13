using System;
using System.IO;
using System.Linq;
using LingoIsland;
using LingoIsland.Present;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 複查回饋批次之純函式驗收：筆記/歷史條目編輯重譯（Store.Update*）、條目/查詢視窗顯示偏好
/// （EntryDisplaySettings／ResultDisplaySettings.SyncFrom 界限與縮放推導），及單字/整段查詢提示常數。
/// </summary>
public class ReviewBatchTests
{
    // ---- NotesStore.UpdateEntryContent（編輯原文→重譯回填）----

    private static NoteEntry Note(string original) =>
        NoteEntry.From(new QueryResult(original, "old-ph", "old-tr"), DateTimeOffset.Now);

    [Fact]
    public void UpdateEntryContent_OverwritesThreeFields_ResetsPractice_KeepsIdColorAddedAt()
    {
        var d = new NotesData();
        NotesStore.AddTo(d, Note("helo"));
        var e0 = d.Folders[0].Entries[0];
        // 先給底色與練習分數，模擬既有狀態
        d.Folders[0].Entries[0] = e0 with { Color = "#FF88AA", PracticeScore = 92 };
        var id = e0.Id;
        var addedAt = e0.AddedAt;

        var ok = NotesStore.UpdateEntryContent(d, id, new QueryResult("hello", "həˈloʊ", "哈囉"));

        Assert.True(ok);
        var e = d.Folders[0].Entries[0];
        Assert.Equal("hello", e.Original);
        Assert.Equal("həˈloʊ", e.Phonetic);
        Assert.Equal("哈囉", e.Translation);
        Assert.Equal(-1, e.PracticeScore);       // 原文變更 → 練習失效回未練
        Assert.Equal(id, e.Id);                  // Id 不變（同一則）
        Assert.Equal("#FF88AA", e.Color);        // 底色分類保留
        Assert.Equal(addedAt, e.AddedAt);        // 登記時間保留
    }

    [Fact]
    public void UpdateEntryContent_UnknownId_ReturnsFalse_NoChange()
    {
        var d = new NotesData();
        NotesStore.AddTo(d, Note("hello"));
        Assert.False(NotesStore.UpdateEntryContent(d, "no-such-id", new QueryResult("x", "y", "z")));
        Assert.Equal("hello", d.Folders[0].Entries[0].Original);
    }

    // ---- HistoryStore.UpdateContent ----

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-rb-hist-{Guid.NewGuid():N}.json");

    [Fact]
    public void HistoryUpdateContent_OverwritesThreeFields_KeepsIdAndTimestamp()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            store.Append(new QueryResult("wrold", "wph", "wtr"), 200, DateTimeOffset.UtcNow);
            var before = store.Load()[0];

            store.UpdateContent(before.Id, new QueryResult("world", "wɜːrld", "世界"));

            var after = store.Load()[0];
            Assert.Equal("world", after.Original);
            Assert.Equal("wɜːrld", after.Phonetic);
            Assert.Equal("世界", after.Translation);
            Assert.Equal(before.Id, after.Id);            // Id 不變
            Assert.Equal(before.Timestamp, after.Timestamp); // 時間不變
        }
        finally { File.Delete(path); }
    }

    // ---- EntryDisplaySettings.SyncFrom ----

    [Fact]
    public void EntryDisplaySettings_SyncFrom_AppliesValues()
    {
        EntryDisplaySettings.SyncFrom(new AppConfig("m", 15, "", EntryFontSize: 22, EntryBold: false, EntryWrap: true));
        Assert.Equal(22, EntryDisplaySettings.FontSize);
        Assert.False(EntryDisplaySettings.Bold);
        Assert.True(EntryDisplaySettings.Wrap);
    }

    [Theory]
    [InlineData(7)]    // < 8
    [InlineData(49)]   // > 48
    [InlineData(0)]
    public void EntryDisplaySettings_SyncFrom_OutOfRangeFont_AppliesDefault(double bad)
    {
        EntryDisplaySettings.SyncFrom(new AppConfig("m", 15, "", EntryFontSize: bad));
        Assert.Equal(AppConfig.DefaultEntryFontSize, EntryDisplaySettings.FontSize);
    }

    // ---- ResultDisplaySettings.SyncFrom + 縮放推導 ----

    [Fact]
    public void ResultDisplaySettings_SyncFrom_DerivesPhoneticAndTranslation()
    {
        ResultDisplaySettings.SyncFrom(new AppConfig("m", 15, "", ResultFontSize: 30, ResultHideOnBlur: true));
        Assert.Equal(30, ResultDisplaySettings.FontSize);
        Assert.Equal(26, ResultDisplaySettings.PhoneticSize);    // 基準 −4
        Assert.Equal(28, ResultDisplaySettings.TranslationSize); // 基準 −2
        Assert.True(ResultDisplaySettings.HideOnBlur);
    }

    [Theory]
    [InlineData(13)]   // < 14
    [InlineData(49)]   // > 48
    public void ResultDisplaySettings_SyncFrom_OutOfRangeFont_AppliesDefault(double bad)
    {
        ResultDisplaySettings.SyncFrom(new AppConfig("m", 15, "", ResultFontSize: bad));
        Assert.Equal(AppConfig.DefaultResultFontSize, ResultDisplaySettings.FontSize);
    }

    [Fact]
    public void ResultDisplaySettings_SmallFont_DerivedSizesFloorAt12()
    {
        ResultDisplaySettings.SyncFrom(new AppConfig("m", 15, "", ResultFontSize: 14)); // 14−4=10、14−2=12 → 下限 12
        Assert.Equal(12, ResultDisplaySettings.PhoneticSize);
        Assert.Equal(12, ResultDisplaySettings.TranslationSize);
    }

    // ---- 單字/整段查詢提示常數（三欄結構）----

    [Fact]
    public void WordAndTextPrompts_DemandThreeColumns()
    {
        foreach (var p in new[] { QueryService.WordPrompt, QueryService.TextPrompt })
        {
            Assert.Contains("original", p);
            Assert.Contains("phonetic", p);
            Assert.Contains("translation", p);
        }
    }
}
