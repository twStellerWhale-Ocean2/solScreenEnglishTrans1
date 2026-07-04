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
}
