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
    public void SaveLoad_Roundtrips_ThreeFields()
    {
        var path = TempPath();
        try
        {
            new AppConfig("gpt-4o", 30, "Microsoft Zira Desktop").Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal("gpt-4o", loaded.Model);
            Assert.Equal(30, loaded.TimeoutSec);
            Assert.Equal("Microsoft Zira Desktop", loaded.Voice);
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
