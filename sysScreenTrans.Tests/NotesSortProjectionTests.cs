using System;
using System.Collections.Generic;
using System.Linq;
using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// 筆記排序非破壞式檢視投影（#126）：<see cref="NotesStore.ProjectView"/> 純函式——依模式/方向產顯示序，
/// **不改動輸入**（f.Entries 手動序＝「自訂順序」SSOT）；及 <see cref="FolderSort"/> 預設與每模式各自方向。
/// </summary>
public class NotesSortProjectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NoteEntry E(string id, string original, DateTimeOffset added) =>
        new(id, added, original, "ph", "tr");

    // 儲存序（手動序）＝ a,b,c；原文 banana/Apple/cherry；時間 30/10/20 分
    private static List<NoteEntry> Sample() => new()
    {
        E("a", "banana", T0.AddMinutes(30)),
        E("b", "Apple", T0.AddMinutes(10)),
        E("c", "cherry", T0.AddMinutes(20)),
    };

    private static string Ids(IEnumerable<NoteEntry> es) => string.Join(",", es.Select(e => e.Id));

    [Fact]
    public void Manual_Ascending_KeepsStoredOrder()
        => Assert.Equal("a,b,c", Ids(NotesStore.ProjectView(Sample(), NoteSortMode.Manual, ascending: true)));

    [Fact]
    public void Manual_Descending_ReversesStoredOrder()
        => Assert.Equal("c,b,a", Ids(NotesStore.ProjectView(Sample(), NoteSortMode.Manual, ascending: false)));

    [Fact]
    public void Alpha_Ascending_CaseInsensitiveAZ()
        => Assert.Equal("b,a,c", Ids(NotesStore.ProjectView(Sample(), NoteSortMode.Alpha, ascending: true))); // Apple,banana,cherry

    [Fact]
    public void Alpha_Descending_ZA()
        => Assert.Equal("c,a,b", Ids(NotesStore.ProjectView(Sample(), NoteSortMode.Alpha, ascending: false))); // cherry,banana,Apple

    [Fact]
    public void Time_Ascending_OldToNew()
        => Assert.Equal("b,c,a", Ids(NotesStore.ProjectView(Sample(), NoteSortMode.Time, ascending: true))); // 10,20,30

    [Fact]
    public void Time_Descending_NewToOld()
        => Assert.Equal("a,c,b", Ids(NotesStore.ProjectView(Sample(), NoteSortMode.Time, ascending: false))); // 30,20,10

    [Fact]
    public void Time_NoValue_TreatedAsOldest()
    {
        var src = new List<NoteEntry>
        {
            E("x", "x", T0.AddMinutes(5)),
            E("novalue", "y", default), // AddedAt == default → 視為最舊
        };
        Assert.Equal("novalue", NotesStore.ProjectView(src, NoteSortMode.Time, ascending: true)[0].Id);   // Old→New 排最前
        Assert.Equal("novalue", NotesStore.ProjectView(src, NoteSortMode.Time, ascending: false)[^1].Id); // New→Old 排最後
    }

    [Fact]
    public void Time_StableForEqualTimestamps()
    {
        var t = T0.AddMinutes(15);
        var src = new List<NoteEntry> { E("first", "z", t), E("second", "a", t) };
        Assert.Equal("first,second", Ids(NotesStore.ProjectView(src, NoteSortMode.Time, ascending: true))); // 同時刻維持相對順序（穩定）
    }

    [Fact]
    public void ProjectView_DoesNotMutateInput()
    {
        var src = Sample();
        var before = Ids(src);
        _ = NotesStore.ProjectView(src, NoteSortMode.Alpha, ascending: true);
        _ = NotesStore.ProjectView(src, NoteSortMode.Time, ascending: false);
        _ = NotesStore.ProjectView(src, NoteSortMode.Manual, ascending: false);
        Assert.Equal(before, Ids(src)); // 投影非破壞——手動序（自訂順序 SSOT）不變
    }

    [Fact]
    public void FolderSort_Defaults_ManualAndNewestFirstDate()
    {
        var s = new FolderSort();
        Assert.Equal(NoteSortMode.Manual, s.Mode);
        Assert.True(s.AlphaAsc);   // 字母預設 A→Z
        Assert.False(s.TimeAsc);   // 日期預設 New→Old（新在上）
        Assert.True(s.ManualAsc);
        Assert.True(s.CurrentAscending); // Manual → ManualAsc
    }

    [Fact]
    public void FolderSort_CurrentAscending_TracksActiveMode()
    {
        var s = new FolderSort { Mode = NoteSortMode.Time, TimeAsc = false };
        Assert.False(s.CurrentAscending);
        s.Mode = NoteSortMode.Alpha;
        s.AlphaAsc = true;
        Assert.True(s.CurrentAscending);
    }
}
