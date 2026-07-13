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
public class ContextStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-ctx-{Guid.NewGuid():N}.json");

    [Fact]
    public void Add_FirstIsActive_SecondNot()
    {
        var d = new ContextsData();
        var a = ContextStore.Add(d, "RPG");
        var b = ContextStore.Add(d, "專業軟體");
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
    }

    // ---- #69：情境內各色配色規則 ----

    [Fact]
    public void BuildColorRulesText_FormatsNonEmptyByPaletteOrder()
    {
        var it = new ContextItem();
        it.ColorRules["Blue"] = "悲傷的台詞";
        it.ColorRules["Pink"] = "勇敢或戰鬥";
        var text = ContextStore.BuildColorRulesText(it);
        // 依盤序：Pink 在 Blue 前
        Assert.Equal("Pink = \"勇敢或戰鬥\"; Blue = \"悲傷的台詞\"", text);
    }

    [Fact]
    public void BuildColorRulesText_SkipsBlankDescriptions()
    {
        var it = new ContextItem();
        it.ColorRules["Pink"] = "  ";      // 空白略過
        it.ColorRules["Green"] = "系統訊息";
        Assert.Equal("Green = \"系統訊息\"", ContextStore.BuildColorRulesText(it));
    }

    [Fact]
    public void BuildColorRulesText_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", ContextStore.BuildColorRulesText(null));
        Assert.Equal("", ContextStore.BuildColorRulesText(new ContextItem()));
    }

    [Fact]
    public void ColorRules_Roundtrips_ThroughStore()
    {
        var path = TempPath();
        try
        {
            var store = new ContextStore(path);
            var d = new ContextsData();
            var it = ContextStore.Add(d, "遊戲A");
            it.ColorRules["Yellow"] = "旁白";
            store.Save(d);
            var loaded = store.Load().Items.Single();
            Assert.Equal("旁白", loaded.ColorRules["Yellow"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ActiveColorRules_UsesActiveContext()
    {
        var path = TempPath();
        try
        {
            var store = new ContextStore(path);
            var d = new ContextsData();
            var a = ContextStore.Add(d, "A"); // 首則使用中
            a.ColorRules["Gray"] = "boss";
            var b = ContextStore.Add(d, "B");
            b.ColorRules["Pink"] = "小兵";
            store.Save(d);
            Assert.Equal("Gray = \"boss\"", store.ActiveColorRules()); // A 使用中
        }
        finally { File.Delete(path); }
    }

    // ---- #53：圖片自動填名判斷 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("New Context")]     // 預設佔位符視為「尚未填」
    [InlineData("  New Context ")]  // 前後空白亦視為佔位
    public void ShouldAutoFillName_TrueWhenBlankOrPlaceholder(string? current)
    {
        Assert.True(ContextStore.ShouldAutoFillName(current));
    }

    [Theory]
    [InlineData("薩爾達傳說")]
    [InlineData("我的遊戲")]
    [InlineData("New Context notes")] // 含佔位但非等於＝使用者已鍵入實名
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
