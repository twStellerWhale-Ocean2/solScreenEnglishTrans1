using System;
using System.IO;
using System.Linq;
using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// [modQuery模組] 我的筆記儲存契約（Issue #28，spec#7）：加入去重、資料夾 CRUD、
/// 同夾排序、跨夾移動之純函式，及檔案往返與缺檔/毀損退空之容錯。
/// </summary>
public class NotesStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"screentrans-notes-{Guid.NewGuid():N}.json");

    private static QueryResult R(string original) => new(original, "ph", "tr");

    private static NoteEntry E(string original) => NoteEntry.From(R(original), DateTimeOffset.Now);

    // ---- 去重與加入 ----

    [Fact]
    public void AddTo_AddsToFirstFolderTop()
    {
        var d = new NotesData();
        Assert.Equal(NoteAddResult.Added, NotesStore.AddTo(d, E("hello")));
        Assert.Equal(NoteAddResult.Added, NotesStore.AddTo(d, E("world")));
        Assert.Equal("world", d.Folders[0].Entries[0].Original); // 新在前
        Assert.Equal("hello", d.Folders[0].Entries[1].Original);
    }

    [Theory]
    [InlineData("Hello")]
    [InlineData("  hello  ")]
    [InlineData("HELLO")]
    public void AddTo_DuplicateKey_NormalizedCaseAndSpace(string dup)
    {
        var d = new NotesData();
        NotesStore.AddTo(d, E("hello"));
        Assert.Equal(NoteAddResult.AlreadyExists, NotesStore.AddTo(d, E(dup)));
        Assert.Single(d.Folders[0].Entries);
    }

    [Fact]
    public void AddTo_EmptyOriginal_ReturnsEmpty()
    {
        var d = new NotesData();
        Assert.Equal(NoteAddResult.Empty, NotesStore.AddTo(d, E("   ")));
    }

    [Fact]
    public void Contains_ChecksAcrossFolders()
    {
        var d = new NotesData();
        var f2 = NotesStore.AddFolder(d, "F2");
        NotesStore.AddTo(d, E("alpha"));           // → 第一夾
        f2.Entries.Add(E("beta"));                 // 另一夾
        Assert.True(NotesStore.Contains(d, NoteEntry.KeyOf("BETA")));
        Assert.Equal(NoteAddResult.AlreadyExists, NotesStore.AddTo(d, E("beta")));
    }

    // ---- 資料夾 CRUD ----

    [Fact]
    public void Folder_AddRenameRemove()
    {
        var d = new NotesData();
        var f = NotesStore.AddFolder(d, "RPG");
        Assert.Contains(d.Folders, x => x.Name == "RPG");
        NotesStore.RenameFolder(d, f.Id, "RPG 用語");
        Assert.Equal("RPG 用語", d.Folders.Single(x => x.Id == f.Id).Name);
        NotesStore.RemoveFolder(d, f.Id);
        Assert.DoesNotContain(d.Folders, x => x.Id == f.Id);
    }

    [Fact]
    public void RemoveFolder_LastOne_EnsuresDefaultRemains()
    {
        var d = new NotesData();
        var f = NotesStore.AddFolder(d, "only");
        NotesStore.RemoveFolder(d, f.Id);
        Assert.Single(d.Folders); // Ensure 補回預設夾
        Assert.Equal(NotesStore.DefaultFolderName, d.Folders[0].Name);
    }

    // ---- 條目移動與排序 ----

    [Fact]
    public void MoveEntry_MovesAcrossFolders()
    {
        var d = new NotesData();
        NotesStore.AddTo(d, E("x"));
        var src = d.Folders[0];
        var dst = NotesStore.AddFolder(d, "dst");
        var id = src.Entries[0].Id;

        NotesStore.MoveEntry(d, id, dst.Id);

        Assert.Empty(src.Entries);
        Assert.Single(dst.Entries);
        Assert.Equal("x", dst.Entries[0].Original);
    }

    [Fact]
    public void Reorder_MovesWithinFolder()
    {
        var f = new NoteFolder();
        f.Entries.AddRange(new[] { E("a"), E("b"), E("c") }); // a,b,c
        NotesStore.Reorder(f, 0, 2); // a → 末
        Assert.Equal(new[] { "b", "c", "a" }, f.Entries.Select(x => x.Original).ToArray());
    }

    // ---- #52：順向/反向排序 ----

    [Fact]
    public void SortEntries_Ascending_ByOriginalNatural()
    {
        var f = new NoteFolder();
        f.Entries.AddRange(new[] { E("banana"), E("Apple"), E("cherry") });
        NotesStore.SortEntries(f, ascending: true);
        Assert.Equal(new[] { "Apple", "banana", "cherry" }, f.Entries.Select(x => x.Original).ToArray());
    }

    [Fact]
    public void SortEntries_Descending_ReversesOrder()
    {
        var f = new NoteFolder();
        f.Entries.AddRange(new[] { E("banana"), E("Apple"), E("cherry") });
        NotesStore.SortEntries(f, ascending: false);
        Assert.Equal(new[] { "cherry", "banana", "Apple" }, f.Entries.Select(x => x.Original).ToArray());
    }

    [Fact]
    public void SortEntries_CaseInsensitive_AndNumericAware()
    {
        // 自然排序：大小寫不敏感、數字段依數值（item 2 < item 10）
        var f = new NoteFolder();
        f.Entries.AddRange(new[] { E("item 10"), E("ITEM 2"), E("item 1") });
        NotesStore.SortEntries(f, ascending: true);
        Assert.Equal(new[] { "item 1", "ITEM 2", "item 10" }, f.Entries.Select(x => x.Original).ToArray());
    }

    [Fact]
    public void SortEntries_EmptyFolder_NoThrow()
    {
        var f = new NoteFolder();
        NotesStore.SortEntries(f, ascending: true); // 空夾無為、不擲例外
        Assert.Empty(f.Entries);
    }

    [Fact]
    public void RemoveEntry_RemovesById()
    {
        var d = new NotesData();
        NotesStore.AddTo(d, E("keep"));
        NotesStore.AddTo(d, E("drop"));
        var dropId = d.Folders[0].Entries.Single(e => e.Original == "drop").Id;
        NotesStore.RemoveEntry(d, dropId);
        Assert.Single(d.Folders[0].Entries);
        Assert.Equal("keep", d.Folders[0].Entries[0].Original);
    }

    // ---- 檔案往返與容錯 ----

    [Fact]
    public void AddAndSave_Then_Load_Roundtrips_WithDedup()
    {
        var path = TempPath();
        try
        {
            var store = new NotesStore(path);
            Assert.Equal(NoteAddResult.Added, store.AddAndSave(R("hello world"), DateTimeOffset.Now));
            Assert.Equal(NoteAddResult.AlreadyExists, store.AddAndSave(R("Hello World"), DateTimeOffset.Now));
            var d = store.Load();
            Assert.Single(d.Folders);
            Assert.Single(d.Folders[0].Entries);
            Assert.Equal("hello world", d.Folders[0].Entries[0].Original);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reorder_Persisted_AcrossSaveLoad()
    {
        var path = TempPath();
        try
        {
            var store = new NotesStore(path);
            var d = store.LoadEnsured();
            NotesStore.AddTo(d, E("a"));
            NotesStore.AddTo(d, E("b")); // b,a
            NotesStore.Reorder(d.Folders[0], 0, 1); // → a,b
            store.Save(d);

            var reloaded = store.Load();
            Assert.Equal(new[] { "a", "b" }, reloaded.Folders[0].Entries.Select(x => x.Original).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new NotesStore(TempPath()).Load().Folders);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not json ]");
        try { Assert.Empty(new NotesStore(path).Load().Folders); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ToResult_MapsThreeColumns()
    {
        var r = new NoteEntry("id", DateTimeOffset.Now, "o", "p", "t").ToResult();
        Assert.Equal("o", r.Original);
        Assert.Equal("p", r.Phonetic);
        Assert.Equal("t", r.Translation);
    }

    // ---- 多層樹（Issue #34） ----

    [Fact]
    public void AddSubFolder_NestsUnderParent()
    {
        var d = new NotesData();
        var top = NotesStore.AddFolder(d, "生字");
        var sub = NotesStore.AddSubFolder(d, top.Id, "動詞");
        Assert.NotNull(sub);
        Assert.Contains(top.Folders, f => f.Id == sub!.Id);
        Assert.NotNull(NotesStore.FindFolder(d, sub!.Id)); // 樹走訪找得到
    }

    [Fact]
    public void Contains_And_Remove_Recurse_IntoSubFolders()
    {
        var d = new NotesData();
        var top = NotesStore.AddFolder(d, "生字");
        var sub = NotesStore.AddSubFolder(d, top.Id, "動詞")!;
        sub.Entries.Add(E("run"));
        Assert.True(NotesStore.Contains(d, NoteEntry.KeyOf("RUN"))); // 跨層去重
        var id = sub.Entries[0].Id;
        NotesStore.RemoveEntry(d, id);
        Assert.Empty(sub.Entries);
    }

    [Fact]
    public void MoveFolder_ToTop_And_UnderAnother()
    {
        var d = new NotesData();
        var a = NotesStore.AddFolder(d, "A");
        var b = NotesStore.AddFolder(d, "B");
        var sub = NotesStore.AddSubFolder(d, a.Id, "sub")!;

        Assert.True(NotesStore.MoveFolder(d, sub.Id, b.Id)); // sub 移到 B 下
        Assert.Empty(a.Folders);
        Assert.Contains(b.Folders, f => f.Id == sub.Id);

        Assert.True(NotesStore.MoveFolder(d, sub.Id, null)); // sub 移到頂層
        Assert.Contains(d.Folders, f => f.Id == sub.Id);
        Assert.Empty(b.Folders);
    }

    [Fact]
    public void MoveFolder_IntoSelfOrDescendant_IsRejected_NoCycle()
    {
        var d = new NotesData();
        var a = NotesStore.AddFolder(d, "A");
        var child = NotesStore.AddSubFolder(d, a.Id, "child")!;

        Assert.False(NotesStore.MoveFolder(d, a.Id, a.Id));      // 移入自身
        Assert.False(NotesStore.MoveFolder(d, a.Id, child.Id));  // 移入子孫
        // 樹結構未被破壞：a 仍在頂層、child 仍在 a 之下
        Assert.Contains(d.Folders, f => f.Id == a.Id);
        Assert.Contains(a.Folders, f => f.Id == child.Id);
    }

    [Fact]
    public void NextNewFolderName_Unused_ReturnsBaseName()
    {
        var d = new NotesData();
        NotesStore.AddFolder(d, "我的筆記");

        Assert.Equal("新資料夾", NotesStore.NextNewFolderName(d));
    }

    [Fact]
    public void NextNewFolderName_Taken_AppendsOrdinal_TreeWide()
    {
        var d = new NotesData();
        var a = NotesStore.AddFolder(d, "新資料夾");
        NotesStore.AddSubFolder(d, a.Id, "新資料夾 (2)"); // 占用在子層也算（全樹唯一）

        Assert.Equal("新資料夾 (3)", NotesStore.NextNewFolderName(d));
    }

    [Fact]
    public void NextNewFolderName_GapInOrdinals_TakesFirstFree()
    {
        var d = new NotesData();
        NotesStore.AddFolder(d, "新資料夾");
        NotesStore.AddFolder(d, "新資料夾 (3)"); // (2) 空缺 → 先補 (2)

        Assert.Equal("新資料夾 (2)", NotesStore.NextNewFolderName(d));
    }

    [Fact]
    public void NaturalCompare_DigitRuns_CompareNumerically()
    {
        Assert.True(NotesStore.NaturalCompare("新資料夾 (2)", "新資料夾 (10)") < 0);
        Assert.True(NotesStore.NaturalCompare("a10b", "a2b") > 0);
        Assert.Equal(0, NotesStore.NaturalCompare("f01", "f1")); // 前導零視同數值相等
    }

    [Fact]
    public void NaturalCompare_Letters_CaseInsensitive_PrefixShorterFirst()
    {
        Assert.True(NotesStore.NaturalCompare("apple", "Banana") < 0);
        Assert.True(NotesStore.NaturalCompare("ab", "abc") < 0);
        Assert.Equal(0, NotesStore.NaturalCompare("Same", "same"));
    }

    [Fact]
    public void SortFolders_SortsSiblingsRecursively_ByNaturalName()
    {
        var d = new NotesData();
        var b = NotesStore.AddFolder(d, "B");
        NotesStore.AddFolder(d, "A10");
        NotesStore.AddFolder(d, "A2");
        NotesStore.AddSubFolder(d, b.Id, "z");
        NotesStore.AddSubFolder(d, b.Id, "k");

        NotesStore.SortFolders(d);

        Assert.Equal(new[] { "A2", "A10", "B" }, d.Folders.Select(f => f.Name));
        Assert.Equal(new[] { "k", "z" }, b.Folders.Select(f => f.Name));
    }

    [Fact]
    public void ClearEntries_OnlyTargetFolder_UnknownIdNoop()
    {
        var d = new NotesData();
        var a = NotesStore.AddFolder(d, "A");
        var b = NotesStore.AddFolder(d, "B");
        NotesStore.AddTo(d, NoteEntry.From(new QueryResult("one", "w", "一"), DateTimeOffset.UtcNow)); // 落第一個頂層夾（A）
        b.Entries.Insert(0, NoteEntry.From(new QueryResult("two", "t", "二"), DateTimeOffset.UtcNow));

        NotesStore.ClearEntries(d, a.Id);
        Assert.Empty(a.Entries);
        Assert.Single(b.Entries);

        NotesStore.ClearEntries(d, "no-such-id"); // 無為、不擲例外
        Assert.Single(b.Entries);
    }

    [Fact]
    public void SetEntryColor_FindsAcrossTree_ReplacesColor()
    {
        var d = new NotesData();
        var a = NotesStore.AddFolder(d, "A");
        var sub = NotesStore.AddSubFolder(d, a.Id, "sub")!;
        var e = NoteEntry.From(new QueryResult("hello", "h", "哈囉"), DateTimeOffset.UtcNow);
        sub.Entries.Add(e);

        Assert.True(NotesStore.SetEntryColor(d, e.Id, "#E1EFFB"));
        Assert.Equal("#E1EFFB", sub.Entries[0].Color);
        Assert.Equal("hello", sub.Entries[0].Original); // 其餘欄位不動

        Assert.True(NotesStore.SetEntryColor(d, e.Id, "")); // 清回預設
        Assert.Equal("", sub.Entries[0].Color);

        Assert.False(NotesStore.SetEntryColor(d, "no-such-id", "#FBE4EC")); // 未知 Id 無為
    }

    [Fact]
    public void EntryColor_RoundTripsThroughJson_LegacyMissingFieldDefaultsEmpty()
    {
        var path = TempPath();
        try
        {
            var store = new NotesStore(path);
            var d = new NotesData();
            var f = NotesStore.AddFolder(d, "F");
            var e = NoteEntry.From(new QueryResult("word", "w", "字"), DateTimeOffset.UtcNow);
            f.Entries.Add(e);
            NotesStore.SetEntryColor(d, e.Id, "#FBF3D9");
            store.Save(d);

            var loaded = store.Load();
            Assert.Equal("#FBF3D9", loaded.Folders[0].Entries[0].Color); // 底色隨檔留存

            // 舊檔（無 Color 欄）→ 預設空＝白
            File.WriteAllText(path,
                "{\"Folders\":[{\"Id\":\"f1\",\"Name\":\"F\",\"Entries\":[{\"Id\":\"e1\"," +
                "\"AddedAt\":\"2026-07-04T00:00:00+00:00\",\"Original\":\"old\",\"Phonetic\":\"o\",\"Translation\":\"舊\"}]}]}");
            var legacy = store.Load();
            Assert.Equal("", legacy.Folders[0].Entries[0].Color);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_LegacyFlatJson_UpgradesToTree_NoDataLoss()
    {
        // 舊平面 notes.json（NoteFolder 無 Folders 子夾鍵）
        var path = TempPath();
        File.WriteAllText(path,
            "{\"Folders\":[{\"Id\":\"f1\",\"Name\":\"我的筆記\"," +
            "\"Entries\":[{\"Id\":\"e1\",\"AddedAt\":\"2026-07-04T00:00:00+00:00\"," +
            "\"Original\":\"hello\",\"Phonetic\":\"h\",\"Translation\":\"哈囉\"}]}]}");
        try
        {
            var d = new NotesStore(path).Load();
            Assert.Single(d.Folders);
            Assert.Empty(d.Folders[0].Folders);            // 無子夾＝單層
            Assert.Single(d.Folders[0].Entries);
            Assert.Equal("hello", d.Folders[0].Entries[0].Original);
        }
        finally { File.Delete(path); }
    }
}
