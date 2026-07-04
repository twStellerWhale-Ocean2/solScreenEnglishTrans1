using System;
using System.IO;
using System.Linq;
using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// [modQuery模組] 查詢歷史儲存契約（Issue #13，spec#6）：純函式 <see cref="HistoryStore.Prepend"/>
/// 之新在前/環形截汰，及檔案往返、刪除、清除、缺檔/毀損退空清單之容錯。
/// </summary>
public class HistoryStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"screentrans-hist-{Guid.NewGuid():N}.json");

    private static HistoryEntry Entry(string id) =>
        new(id, DateTimeOffset.UtcNow, "orig-" + id, "ph", "tr");

    // ---- 純函式：排序與環形截汰 ----

    [Fact]
    public void Prepend_PutsNewestFirst()
    {
        var r = HistoryStore.Prepend(new[] { Entry("old") }, Entry("new"), 200);
        Assert.Equal("new", r[0].Id);
        Assert.Equal("old", r[1].Id);
    }

    [Fact]
    public void Prepend_OverCap_TruncatesOldest()
    {
        var existing = new[] { Entry("0"), Entry("1"), Entry("2") }; // 0 為最新
        var r = HistoryStore.Prepend(existing, Entry("new"), 2);
        Assert.Equal(2, r.Count);
        Assert.Equal("new", r[0].Id); // 新在前
        Assert.Equal("0", r[1].Id);   // 舊者（1、2）被截汰
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Prepend_NonPositiveMax_UsesDefaultMax(int badMax)
    {
        var existing = Enumerable.Range(0, HistoryStore.DefaultMax).Select(i => Entry("e" + i)).ToArray();
        var r = HistoryStore.Prepend(existing, Entry("new"), badMax);
        Assert.Equal(HistoryStore.DefaultMax, r.Count); // 非正上限套用預設 200
        Assert.Equal("new", r[0].Id);
        Assert.DoesNotContain(r, x => x.Id == "e" + (HistoryStore.DefaultMax - 1)); // 最舊被汰
    }

    // ---- 檔案往返與容錯 ----

    [Fact]
    public void Append_Then_Load_Roundtrips()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            store.Append(new QueryResult("hello", "h333loU", "哈囉"), 200, DateTimeOffset.Now);
            var list = store.Load();
            Assert.Single(list);
            Assert.Equal("hello", list[0].Original);
            Assert.Equal("哈囉", list[0].Translation);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Append_OverConfiguredMax_KeepsNewestOnly()
    {
        var path = TempPath();
        var now = DateTimeOffset.Now;
        try
        {
            var store = new HistoryStore(path);
            store.Append(new QueryResult("a", "", ""), 2, now);
            store.Append(new QueryResult("b", "", ""), 2, now);
            store.Append(new QueryResult("c", "", ""), 2, now);
            var list = store.Load();
            Assert.Equal(2, list.Count);
            Assert.Equal("c", list[0].Original); // 最新在前
            Assert.Equal("b", list[1].Original);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Delete_RemovesById_LeavesOthers()
    {
        var path = TempPath();
        var now = DateTimeOffset.Now;
        try
        {
            var store = new HistoryStore(path);
            store.Append(new QueryResult("a", "", ""), 200, now);
            store.Append(new QueryResult("b", "", ""), 200, now);
            var aId = store.Load().Single(e => e.Original == "a").Id;

            store.Delete(aId);

            var after = store.Load();
            Assert.Single(after);
            Assert.Equal("b", after[0].Original);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Clear_EmptiesHistory()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            store.Append(new QueryResult("a", "", ""), 200, DateTimeOffset.Now);
            store.Append(new QueryResult("b", "", ""), 200, DateTimeOffset.Now);

            store.Clear();

            Assert.Empty(store.Load());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new HistoryStore(TempPath()).Load()); // 不建檔
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ this is not valid json ]");
        try
        {
            Assert.Empty(new HistoryStore(path).Load()); // 毀損退空清單、不致命
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Delete_OnMissingFile_DoesNotThrow()
    {
        var ex = Record.Exception(() => new HistoryStore(TempPath()).Delete("nope"));
        Assert.Null(ex); // 缺檔刪除靜默降級
    }

    [Fact]
    public void ToResult_MapsThreeColumns()
    {
        var r = new HistoryEntry("id", DateTimeOffset.Now, "o", "p", "t").ToResult();
        Assert.Equal("o", r.Original);
        Assert.Equal("p", r.Phonetic);
        Assert.Equal("t", r.Translation);
    }
}
