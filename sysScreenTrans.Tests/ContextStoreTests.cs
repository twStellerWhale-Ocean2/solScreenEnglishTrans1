using System;
using System.IO;
using System.Linq;
using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// [modQuery模組] 情境儲存契約（Issue #36，spec#9）：CRUD、單一使用中、注入用 ActiveText、
/// 舊 paramContextHint 相容遷移，及檔案往返與缺檔/毀損退空之容錯。
/// </summary>
public class ContextStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"screentrans-ctx-{Guid.NewGuid():N}.json");

    [Fact]
    public void Add_FirstIsActive_SecondNot()
    {
        var d = new ContextsData();
        var a = ContextStore.Add(d, "RPG");
        var b = ContextStore.Add(d, "專業軟體");
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
    }

    // ---- #53：圖片自動填名判斷 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("新情境")]     // 預設佔位符視為「尚未填」
    [InlineData("  新情境 ")]  // 前後空白亦視為佔位
    public void ShouldAutoFillName_TrueWhenBlankOrPlaceholder(string? current)
    {
        Assert.True(ContextStore.ShouldAutoFillName(current));
    }

    [Theory]
    [InlineData("薩爾達傳說")]
    [InlineData("我的遊戲")]
    [InlineData("新情境備註")] // 含佔位但非等於＝使用者已鍵入實名
    public void ShouldAutoFillName_FalseWhenUserNamed(string current)
    {
        Assert.False(ContextStore.ShouldAutoFillName(current));
    }

    [Fact]
    public void SetActive_MakesSingleActive()
    {
        var d = new ContextsData();
        var a = ContextStore.Add(d, "A");
        var b = ContextStore.Add(d, "B");
        ContextStore.SetActive(d, b.Id);
        Assert.False(ContextStore.Find(d, a.Id)!.IsActive);
        Assert.True(ContextStore.Find(d, b.Id)!.IsActive);
        Assert.Equal(b.Id, ContextStore.GetActive(d)!.Id);
    }

    [Fact]
    public void UpdateText_And_ActiveText()
    {
        var d = new ContextsData();
        var a = ContextStore.Add(d, "RPG");
        ContextStore.UpdateText(d, a.Id, "中世紀奇幻 RPG，用遊戲用語翻譯");
        Assert.Equal("中世紀奇幻 RPG，用遊戲用語翻譯", ContextStore.ActiveText(d));
    }

    [Fact]
    public void ActiveText_NoActive_Empty()
    {
        var d = new ContextsData();
        var a = ContextStore.Add(d, "A");
        a.IsActive = false;
        Assert.Equal("", ContextStore.ActiveText(d));
    }

    [Fact]
    public void Rename_And_Remove()
    {
        var d = new ContextsData();
        var a = ContextStore.Add(d, "A");
        ContextStore.Rename(d, a.Id, "Alpha");
        Assert.Equal("Alpha", ContextStore.Find(d, a.Id)!.Name);
        var removed = ContextStore.Remove(d, a.Id);
        Assert.Equal(a.Id, removed!.Id);
        Assert.Empty(d.Items);
    }

    [Fact]
    public void Migrate_EmptyList_WithLegacyHint_CreatesDefaultActive()
    {
        var d = new ContextsData();
        Assert.True(ContextStore.Migrate(d, "中世紀奇幻 RPG"));
        Assert.Single(d.Items);
        Assert.True(d.Items[0].IsActive);
        Assert.Equal("中世紀奇幻 RPG", d.Items[0].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Migrate_NoHint_DoesNothing(string? hint)
    {
        var d = new ContextsData();
        Assert.False(ContextStore.Migrate(d, hint));
        Assert.Empty(d.Items);
    }

    [Fact]
    public void Migrate_NonEmptyList_DoesNotMigrate()
    {
        var d = new ContextsData();
        ContextStore.Add(d, "existing");
        Assert.False(ContextStore.Migrate(d, "legacy"));
        Assert.Single(d.Items);
    }

    [Fact]
    public void SaveLoad_Roundtrips()
    {
        var path = TempPath();
        try
        {
            var store = new ContextStore(path);
            var d = new ContextsData();
            var a = ContextStore.Add(d, "RPG");
            ContextStore.UpdateText(d, a.Id, "遊戲用語");
            store.Save(d);
            Assert.Equal("遊戲用語", store.ActiveText());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_Missing_Or_Corrupt_ReturnsEmpty()
    {
        Assert.Empty(new ContextStore(TempPath()).Load().Items);
        var path = TempPath();
        File.WriteAllText(path, "{ not json ]");
        try { Assert.Empty(new ContextStore(path).Load().Items); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadMigrated_AppliesAndPersists()
    {
        var path = TempPath();
        try
        {
            var store = new ContextStore(path);
            var d = store.LoadMigrated("中世紀奇幻 RPG");
            Assert.Single(d.Items);
            Assert.Equal("中世紀奇幻 RPG", store.ActiveText()); // 已存回
        }
        finally { File.Delete(path); }
    }
}
