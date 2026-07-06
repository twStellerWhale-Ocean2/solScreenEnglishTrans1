using System;
using System.IO;
using System.Linq;
using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// 發音練習分數之儲存純函式（[modQuery模組] 我的筆記儲存契約，spec#10）：
/// SetPracticeScore／ResetFolderPractice／舊檔相容（缺欄位＝ -1）。
/// </summary>
public class NotesStorePracticeTests
{
    private static NoteEntry E(string original) => NoteEntry.From(new QueryResult(original, "ph", "tr"), DateTimeOffset.Now);

    [Fact]
    public void NoteEntry_DefaultPracticeScore_IsMinusOne()
    {
        Assert.Equal(-1, E("hello").PracticeScore); // 未練
    }

    [Fact]
    public void SetPracticeScore_UpdatesTargetEntryOnly()
    {
        var d = new NotesData();
        NotesStore.AddTo(d, E("hello"));
        NotesStore.AddTo(d, E("world"));
        var target = d.Folders[0].Entries.First(e => e.Original == "hello");
        Assert.True(NotesStore.SetPracticeScore(d, target.Id, 88));
        Assert.Equal(88, d.Folders[0].Entries.First(e => e.Original == "hello").PracticeScore);
        Assert.Equal(-1, d.Folders[0].Entries.First(e => e.Original == "world").PracticeScore); // 他筆不動
    }

    [Fact]
    public void SetPracticeScore_UnknownId_ReturnsFalse()
    {
        var d = new NotesData();
        NotesStore.AddTo(d, E("hello"));
        Assert.False(NotesStore.SetPracticeScore(d, "no-such-id", 90));
    }

    [Fact]
    public void SetPracticeScore_FindsAcrossSubfolders()
    {
        var d = new NotesData();
        var top = NotesStore.AddFolder(d, "Top");
        var sub = NotesStore.AddSubFolder(d, top.Id, "Sub")!;
        var e = E("deep");
        sub.Entries.Add(e);
        Assert.True(NotesStore.SetPracticeScore(d, e.Id, 70));
        Assert.Equal(70, sub.Entries[0].PracticeScore);
    }

    [Fact]
    public void ResetFolderPractice_ResetsOnlyThatFolder()
    {
        var d = new NotesData();
        var a = NotesStore.AddFolder(d, "A");
        var b = NotesStore.AddFolder(d, "B");
        a.Entries.Add(E("a1") with { PracticeScore = 90 });
        b.Entries.Add(E("b1") with { PracticeScore = 85 });
        NotesStore.ResetFolderPractice(d, a.Id);
        Assert.Equal(-1, a.Entries[0].PracticeScore); // 歸零
        Assert.Equal(85, b.Entries[0].PracticeScore); // 他夾不動
    }

    [Fact]
    public void ResetFolderPractice_DoesNotTouchSubfolders()
    {
        var d = new NotesData();
        var top = NotesStore.AddFolder(d, "Top");
        var sub = NotesStore.AddSubFolder(d, top.Id, "Sub")!;
        top.Entries.Add(E("t") with { PracticeScore = 90 });
        sub.Entries.Add(E("s") with { PracticeScore = 90 });
        NotesStore.ResetFolderPractice(d, top.Id);
        Assert.Equal(-1, top.Entries[0].PracticeScore);
        Assert.Equal(90, sub.Entries[0].PracticeScore); // 子夾不含在該夾 Entries、不動
    }

    [Fact]
    public void ResetFolderPractice_UnknownFolder_NoOp()
    {
        var d = new NotesData();
        var a = NotesStore.AddFolder(d, "A");
        a.Entries.Add(E("x") with { PracticeScore = 50 });
        NotesStore.ResetFolderPractice(d, "no-such"); // 無為
        Assert.Equal(50, a.Entries[0].PracticeScore);
    }

    [Fact]
    public void PracticeScore_Persists_AcrossSaveLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"st-notes-{Guid.NewGuid():N}.json");
        try
        {
            var store = new NotesStore(path);
            var d = store.LoadEnsured();
            d.Folders[0].Entries.Add(E("persist me") with { PracticeScore = 77 });
            store.Save(d);
            var reloaded = new NotesStore(path).Load();
            Assert.Equal(77, reloaded.Folders[0].Entries[0].PracticeScore);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OldNotesJson_WithoutPracticeScore_DefaultsToMinusOne()
    {
        // 舊 notes.json 無 PracticeScore 欄 → 反序列化用建構子預設 -1（相容、不失資料）
        var path = Path.Combine(Path.GetTempPath(), $"st-notes-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                "{\"Folders\":[{\"Id\":\"f1\",\"Name\":\"My Notes\",\"Folders\":[],\"Entries\":[" +
                "{\"Id\":\"e1\",\"AddedAt\":\"2026-01-01T00:00:00+00:00\",\"Original\":\"old\"," +
                "\"Phonetic\":\"\",\"Translation\":\"\",\"Color\":\"\"}]}]}");
            var d = new NotesStore(path).Load();
            Assert.Equal(-1, d.Folders[0].Entries[0].PracticeScore);
            Assert.Equal("old", d.Folders[0].Entries[0].Original); // 不失資料
        }
        finally { File.Delete(path); }
    }
}
