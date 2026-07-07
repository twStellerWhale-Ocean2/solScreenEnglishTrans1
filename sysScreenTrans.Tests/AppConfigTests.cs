using System;
using System.IO;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// [etyCfg自訂sysScreenTrans組態] 往返與容錯（Issue #9：移除 paramTtsProvider／paramTtsModel）。
/// </summary>
public class AppConfigTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"screentrans-cfg-{Guid.NewGuid():N}.json");

    [Fact]
    public void SaveLoad_Roundtrips_AllFields()
    {
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o", 30, "Microsoft Zira Desktop", 3).Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal("gpt-4o", loaded.Model);
            Assert.Equal(30, loaded.TimeoutSec);
            Assert.Equal("Microsoft Zira Desktop", loaded.Voice);
            Assert.Equal(3, loaded.MaxRetries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingMaxRetries_DefaultsToTwo()
    {
        // 舊 appsettings 無 paramQueryMaxRetries → 用預設 2（向後相容）
        var path = TempPath();
        File.WriteAllText(path, "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\"}");
        try
        {
            var cfg = AppConfig.Load(path);
            Assert.Equal(2, cfg.MaxRetries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_Writes_MaxRetriesKey()
    {
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o-mini", 15, "", 2).Save(path);
            Assert.Contains("paramQueryMaxRetries", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_DoesNotWrite_LegacyTtsProviderModelKeys()
    {
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o-mini", 15, "").Save(path);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("paramTtsProvider", json);
            Assert.DoesNotContain("paramTtsModel", json);
            Assert.Contains("paramTtsVoice", json);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void Load_NonPositiveTimeout_AppliesSafeFloor(int badTimeout)
    {
        // paramQueryTimeoutSec 非正值會使 CancelAfter(0/負) 即刻取消、每次查詢立即逾時（Issue #8）
        // → 讀取邊界套用安全下限 15，令查詢仍以合理逾時運作。
        var path = TempPath();
        File.WriteAllText(path,
            "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":" + badTimeout + ",\"paramTtsVoice\":\"\"}");
        try
        {
            var cfg = AppConfig.Load(path);
            Assert.Equal(15, cfg.TimeoutSec);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_PositiveTimeout_KeptAsIs()
    {
        // 合法正值不受防呆影響。
        var path = TempPath();
        File.WriteAllText(path, "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":45,\"paramTtsVoice\":\"\"}");
        try
        {
            Assert.Equal(45, AppConfig.Load(path).TimeoutSec);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_Roundtrips_Hotkey()
    {
        // 喚起快捷鍵綁定往返（Issue #10）
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o-mini", 15, "", 2, "Ctrl+Shift+F").Save(path);
            Assert.Equal("Ctrl+Shift+F", AppConfig.Load(path).Hotkey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingHotkey_DefaultsToAltL()
    {
        // 舊 appsettings 無 paramHotkey → 預設 Alt+L（向後相容）
        var path = TempPath();
        File.WriteAllText(path, "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\"}");
        try
        {
            Assert.Equal("Alt+L", AppConfig.Load(path).Hotkey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_LegacyHotkeyPoint_IgnoredGracefully()
    {
        // Issue #90 移除第二熱鍵：舊 appsettings 仍含 paramHotkeyPoint → 忽略該鍵、不報錯（向後相容）
        var path = TempPath();
        File.WriteAllText(path,
            "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\"," +
            "\"paramHotkey\":\"Ctrl+Shift+F\",\"paramHotkeyPoint\":\"Ctrl+Shift+A\"}");
        try
        {
            var cfg = AppConfig.Load(path);
            Assert.Equal("gpt-4o", cfg.Model);
            Assert.Equal("Ctrl+Shift+F", cfg.Hotkey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_DoesNotWrite_HotkeyPointKey()
    {
        // Issue #90：不再寫出 paramHotkeyPoint
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o-mini", 15, "").Save(path);
            Assert.DoesNotContain("paramHotkeyPoint", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_Writes_HotkeyKey()
    {
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o-mini", 15, "", 2, "Mouse:Middle").Save(path);
            Assert.Contains("paramHotkey", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = AppConfig.Load(TempPath()); // 不建檔
        Assert.Equal("gpt-4o-mini", cfg.Model);
        Assert.Equal(15, cfg.TimeoutSec);
        Assert.Equal("", cfg.Voice);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsDefaults()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ this is not valid json ");
        try
        {
            var cfg = AppConfig.Load(path);
            Assert.Equal("gpt-4o-mini", cfg.Model);
            Assert.Equal(15, cfg.TimeoutSec);
            Assert.Equal("", cfg.Voice);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingHistoryMax_DefaultsTo200()
    {
        // 舊 appsettings 無 paramHistoryMax → 用預設 200（向後相容，Issue #13）
        var path = TempPath();
        File.WriteAllText(path, "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\"}");
        try
        {
            Assert.Equal(200, AppConfig.Load(path).HistoryMax);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public void Load_NonPositiveHistoryMax_AppliesDefault(int badMax)
    {
        // 非正上限會清空或無界成長歷史 → 讀取邊界套用預設 200（Issue #13）
        var path = TempPath();
        File.WriteAllText(path,
            "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\",\"paramHistoryMax\":" + badMax + "}");
        try
        {
            Assert.Equal(200, AppConfig.Load(path).HistoryMax);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_PositiveHistoryMax_KeptAsIs()
    {
        var path = TempPath();
        File.WriteAllText(path,
            "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\",\"paramHistoryMax\":50}");
        try
        {
            Assert.Equal(50, AppConfig.Load(path).HistoryMax);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_Writes_HistoryMaxKey_AndRoundtrips()
    {
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o-mini", 15, "", 2, "Alt+L", 30).Save(path);
            Assert.Contains("paramHistoryMax", File.ReadAllText(path));
            Assert.Equal(30, AppConfig.Load(path).HistoryMax);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingContextHint_DefaultsToEmpty()
    {
        var path = TempPath();
        File.WriteAllText(path, "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\"}");
        try
        {
            Assert.Equal("", AppConfig.Load(path).Context);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_Roundtrips_ContextHint()
    {
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o-mini", 15, "", 2, "Alt+L", 200, "中世紀奇幻 RPG").Save(path);
            Assert.Contains("paramContextHint", File.ReadAllText(path));
            Assert.Equal("中世紀奇幻 RPG", AppConfig.Load(path).Context);
        }
        finally { File.Delete(path); }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"screentrans-cfgdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ResolveSettingsPath_LegacyOnly_MigratesToAppData()
    {
        // Issue #51：%APPDATA% 尚無檔而 exe 旁有舊檔 → 一次性複製（升級不洗設定），回傳 appData 路徑
        var legacyDir = TempDir();
        var appDataDir = TempDir();
        var legacy = Path.Combine(legacyDir, "appsettings.json");
        var target = Path.Combine(appDataDir, "sub", "appsettings.json"); // 目標目錄不存在也要能建
        File.WriteAllText(legacy, "{\"paramModel\":\"gpt-4o\"}");
        try
        {
            Assert.Equal(target, AppConfig.ResolveSettingsPath(legacy, target));
            Assert.True(File.Exists(target));
            Assert.Equal("gpt-4o", AppConfig.Load(target).Model);
        }
        finally
        {
            Directory.Delete(legacyDir, true);
            Directory.Delete(appDataDir, true);
        }
    }

    [Fact]
    public void ResolveSettingsPath_AppDataExists_DoesNotOverwrite()
    {
        // 兩邊都有檔 → %APPDATA% 為準（exe 旁為發佈內附預設檔，不得倒灌覆蓋使用者設定）
        var legacyDir = TempDir();
        var appDataDir = TempDir();
        var legacy = Path.Combine(legacyDir, "appsettings.json");
        var target = Path.Combine(appDataDir, "appsettings.json");
        File.WriteAllText(legacy, "{\"paramModel\":\"legacy-model\"}");
        File.WriteAllText(target, "{\"paramModel\":\"user-model\"}");
        try
        {
            Assert.Equal(target, AppConfig.ResolveSettingsPath(legacy, target));
            Assert.Equal("user-model", AppConfig.Load(target).Model);
        }
        finally
        {
            Directory.Delete(legacyDir, true);
            Directory.Delete(appDataDir, true);
        }
    }

    [Fact]
    public void ResolveSettingsPath_NeitherExists_ReturnsAppDataPath_NoFileCreated()
    {
        // 全新安裝且無舊檔 → 不產檔（Load 缺檔退預設、Save 時才建）
        var appDataDir = TempDir();
        var target = Path.Combine(appDataDir, "appsettings.json");
        try
        {
            Assert.Equal(target, AppConfig.ResolveSettingsPath(
                Path.Combine(appDataDir, "no-such-legacy.json"), target));
            Assert.False(File.Exists(target));
        }
        finally { Directory.Delete(appDataDir, true); }
    }

    [Fact]
    public void Save_CreatesMissingParentDirectory()
    {
        // Issue #51：首次於 %APPDATA%\ScreenTrans 存檔時目錄可能不存在
        var dir = TempDir();
        var path = Path.Combine(dir, "nested", "appsettings.json");
        try
        {
            new AppConfig("gpt-4o-mini", 15, "").Save(path);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_LegacyConfigWithTtsProviderModel_IgnoresExtraKeys()
    {
        // 舊 appsettings 仍含 paramTtsProvider／paramTtsModel → 應被忽略、不報錯（向後相容）
        var path = TempPath();
        File.WriteAllText(path,
            "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20," +
            "\"paramTtsProvider\":\"openai\",\"paramTtsModel\":\"gpt-4o-mini-tts\"," +
            "\"paramTtsVoice\":\"Microsoft Zira Desktop\"}");
        try
        {
            var cfg = AppConfig.Load(path);
            Assert.Equal("gpt-4o", cfg.Model);
            Assert.Equal(20, cfg.TimeoutSec);
            Assert.Equal("Microsoft Zira Desktop", cfg.Voice);
        }
        finally { File.Delete(path); }
    }

    // ---- 發音練習組態（spec#10）----

    [Fact]
    public void Load_MissingPronKeys_DefaultsToThreshold80AndAudioModel()
    {
        // 舊 appsettings 無發音鍵 → 門檻預設 80、模型預設音訊模型（向後相容）
        var path = TempPath();
        File.WriteAllText(path, "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\"}");
        try
        {
            var cfg = AppConfig.Load(path);
            Assert.Equal(80, cfg.PronPassThreshold);
            Assert.Equal("gpt-audio-1.5", cfg.PronModel);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(999)]
    public void Load_OutOfRangePronThreshold_AppliesDefault(int bad)
    {
        var path = TempPath();
        File.WriteAllText(path,
            "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\",\"paramPronPassThreshold\":" + bad + "}");
        try
        {
            Assert.Equal(80, AppConfig.Load(path).PronPassThreshold);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(65)]
    public void Load_InRangePronThreshold_KeptAsIs(int v)
    {
        var path = TempPath();
        File.WriteAllText(path,
            "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\",\"paramPronPassThreshold\":" + v + "}");
        try
        {
            Assert.Equal(v, AppConfig.Load(path).PronPassThreshold);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_Roundtrips_PronThresholdAndModel()
    {
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o-mini", 15, "", 2, "Alt+L", 200, "", 65, "gpt-4o-audio-preview").Save(path);
            var json = File.ReadAllText(path);
            Assert.Contains("paramPronPassThreshold", json);
            Assert.Contains("paramPronModel", json);
            var cfg = AppConfig.Load(path);
            Assert.Equal(65, cfg.PronPassThreshold);
            Assert.Equal("gpt-4o-audio-preview", cfg.PronModel);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_LegacyPronAudioMini_MigratesToCurrentAudioModel()
    {
        var path = TempPath();
        File.WriteAllText(path,
            "{\"paramModel\":\"gpt-4o\",\"paramQueryTimeoutSec\":20,\"paramTtsVoice\":\"\",\"paramPronModel\":\"gpt-audio-mini\"}");
        try
        {
            Assert.Equal("gpt-audio-1.5", AppConfig.Load(path).PronModel);
        }
        finally { File.Delete(path); }
    }
}
