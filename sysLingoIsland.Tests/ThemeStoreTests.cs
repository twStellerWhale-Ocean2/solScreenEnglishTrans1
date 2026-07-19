using System;
using System.IO;
using System.Linq;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modQuery模組] 情境儲存契約（Issue #36，spec#9）：CRUD、單一使用中、注入用 ActiveText、
/// 舊 paramContextHint 相容遷移，及檔案往返與缺檔/毀損退空之容錯。
/// </summary>
public class ThemeStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-ctx-{Guid.NewGuid():N}.json");

    [Fact]
    public void Add_FirstIsActive_SecondNot()
    {
        var d = new ThemesData();
        var a = ThemeStore.Add(d, "RPG");
        var b = ThemeStore.Add(d, "專業軟體");
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
    }

    // ---- #69／#189-checklist：主題 12 色可編輯色盤（hex＋描述） ----

    [Fact]
    public void BuildColorRulesText_FormatsNonBlankBySlotOrder_HexKeyed()
    {
        var it = new ThemeItem();
        it.Colors.Add(new ThemeColor { Hex = "#FBE4EC", Description = "勇敢或戰鬥" });
        it.Colors.Add(new ThemeColor { Hex = "#E1EFFB", Description = "悲傷的台詞" });
        // 依槽序、hex 為鍵；Ensure 補足 12 空槽（空描述略過）
        Assert.Equal("#FBE4EC = \"勇敢或戰鬥\"; #E1EFFB = \"悲傷的台詞\"", ThemeStore.BuildColorRulesText(it));
    }

    [Fact]
    public void BuildColorRulesText_SkipsBlankDescriptions()
    {
        var it = new ThemeItem();
        it.Colors.Add(new ThemeColor { Hex = "#FBE4EC", Description = "  " });   // 空白略過
        it.Colors.Add(new ThemeColor { Hex = "#E4F5E9", Description = "系統訊息" });
        Assert.Equal("#E4F5E9 = \"系統訊息\"", ThemeStore.BuildColorRulesText(it));
    }

    [Fact]
    public void BuildColorRulesText_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", ThemeStore.BuildColorRulesText(null));
        Assert.Equal("", ThemeStore.BuildColorRulesText(new ThemeItem())); // Ensure 補 12 空槽→全略過
    }

    [Fact]
    public void EnsureColors_MigratesOldColorRules_ThenPadsTo12()
    {
        var it = new ThemeItem();
        it.ColorRules["Blue"] = "sad";   // 舊資料
        ThemeColors.Ensure(it);
        Assert.Equal(ThemeColors.Count, it.Colors.Count);                       // 恆 12 槽
        Assert.Contains(it.Colors, c => c.Hex == "#E1EFFB" && c.Description == "sad"); // Blue→hex 遷移
        ThemeColors.Ensure(it);                                                 // 再呼叫不重複遷移（Colors 已非空）
        Assert.Equal(ThemeColors.Count, it.Colors.Count);
    }

    [Fact]
    public void Colors_Roundtrip_ThroughStore()
    {
        var path = TempPath();
        try
        {
            var store = new ThemeStore(path);
            var d = new ThemesData();
            var it = ThemeStore.Add(d, "遊戲A");
            ThemeColors.Ensure(it);
            it.Colors[3].Hex = "#FDD835"; it.Colors[3].Description = "旁白";
            store.Save(d);
            var loaded = store.Load().Items.Single();
            Assert.Equal("旁白", loaded.Colors[3].Description);
            Assert.Equal("#FDD835", loaded.Colors[3].Hex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ActiveColorRules_UsesActiveContext()
    {
        var path = TempPath();
        try
        {
            var store = new ThemeStore(path);
            var d = new ThemesData();
            var a = ThemeStore.Add(d, "A"); // 首則使用中
            ThemeColors.Ensure(a); a.Colors[0].Hex = "#111111"; a.Colors[0].Description = "boss";
            var b = ThemeStore.Add(d, "B");
            ThemeColors.Ensure(b); b.Colors[0].Description = "小兵";
            store.Save(d);
            Assert.Equal("#111111 = \"boss\"", store.ActiveColorRules()); // A 使用中
        }
        finally { File.Delete(path); }
    }

    // ---- #53：圖片自動填名判斷 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("New Theme")]     // 預設佔位符視為「尚未填」
    [InlineData("  New Theme ")]  // 前後空白亦視為佔位
    public void ShouldAutoFillName_TrueWhenBlankOrPlaceholder(string? current)
    {
        Assert.True(ThemeStore.ShouldAutoFillName(current));
    }

    [Theory]
    [InlineData("薩爾達傳說")]
    [InlineData("我的遊戲")]
    [InlineData("New Theme notes")] // 含佔位但非等於＝使用者已鍵入實名
    public void ShouldAutoFillName_FalseWhenUserNamed(string current)
    {
        Assert.False(ThemeStore.ShouldAutoFillName(current));
    }

    [Fact]
    public void SetActive_MakesSingleActive()
    {
        var d = new ThemesData();
        var a = ThemeStore.Add(d, "A");
        var b = ThemeStore.Add(d, "B");
        ThemeStore.SetActive(d, b.Id);
        Assert.False(ThemeStore.Find(d, a.Id)!.IsActive);
        Assert.True(ThemeStore.Find(d, b.Id)!.IsActive);
        Assert.Equal(b.Id, ThemeStore.GetActive(d)!.Id);
    }

    [Fact]
    public void UpdateText_And_ActiveText()
    {
        var d = new ThemesData();
        var a = ThemeStore.Add(d, "RPG");
        ThemeStore.UpdateText(d, a.Id, "中世紀奇幻 RPG，用遊戲用語翻譯");
        Assert.Equal("中世紀奇幻 RPG，用遊戲用語翻譯", ThemeStore.ActiveText(d));
    }

    [Fact]
    public void ActiveText_NoActive_Empty()
    {
        var d = new ThemesData();
        var a = ThemeStore.Add(d, "A");
        a.IsActive = false;
        Assert.Equal("", ThemeStore.ActiveText(d));
    }

    [Fact]
    public void Rename_And_Remove()
    {
        var d = new ThemesData();
        var a = ThemeStore.Add(d, "A");
        ThemeStore.Rename(d, a.Id, "Alpha");
        Assert.Equal("Alpha", ThemeStore.Find(d, a.Id)!.Name);
        var removed = ThemeStore.Remove(d, a.Id);
        Assert.Equal(a.Id, removed!.Id);
        Assert.Empty(d.Items);
    }

    [Fact]
    public void Migrate_EmptyList_WithLegacyHint_CreatesDefaultActive()
    {
        var d = new ThemesData();
        Assert.True(ThemeStore.Migrate(d, "中世紀奇幻 RPG"));
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
        var d = new ThemesData();
        Assert.False(ThemeStore.Migrate(d, hint));
        Assert.Empty(d.Items);
    }

    [Fact]
    public void Migrate_NonEmptyList_DoesNotMigrate()
    {
        var d = new ThemesData();
        ThemeStore.Add(d, "existing");
        Assert.False(ThemeStore.Migrate(d, "legacy"));
        Assert.Single(d.Items);
    }

    [Fact]
    public void SaveLoad_Roundtrips()
    {
        var path = TempPath();
        try
        {
            var store = new ThemeStore(path);
            var d = new ThemesData();
            var a = ThemeStore.Add(d, "RPG");
            ThemeStore.UpdateText(d, a.Id, "遊戲用語");
            store.Save(d);
            Assert.Equal("遊戲用語", store.ActiveText());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_Missing_Or_Corrupt_ReturnsEmpty()
    {
        Assert.Empty(new ThemeStore(TempPath()).Load().Items);
        var path = TempPath();
        File.WriteAllText(path, "{ not json ]");
        try { Assert.Empty(new ThemeStore(path).Load().Items); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadMigrated_AppliesAndPersists()
    {
        var path = TempPath();
        try
        {
            var store = new ThemeStore(path);
            var d = store.LoadMigrated("中世紀奇幻 RPG");
            Assert.Single(d.Items);
            Assert.Equal("中世紀奇幻 RPG", store.ActiveText()); // 已存回
        }
        finally { File.Delete(path); }
    }
}
