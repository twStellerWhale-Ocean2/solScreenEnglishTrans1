using System.IO;
using System.Text.Json;

namespace ScreenTrans;

/// <summary>appsettings.json 非機密偏好（[etyCfg自訂sysScreenTrans組態] D 類）。缺檔或欄位用預設。</summary>
public sealed record AppConfig(string Model, int TimeoutSec, string Voice)
{
    public static AppConfig Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            return new AppConfig(
                r.TryGetProperty("paramModel", out var m) ? m.GetString() ?? "gpt-4o-mini" : "gpt-4o-mini",
                r.TryGetProperty("paramQueryTimeoutSec", out var t) ? t.GetInt32() : 15,
                r.TryGetProperty("paramTtsVoice", out var v) ? v.GetString() ?? "" : "");
        }
        catch
        {
            return new AppConfig("gpt-4o-mini", 15, "");
        }
    }

    /// <summary>寫回 appsettings.json（僅非機密參數；金鑰一律走環境變數、不落地）。</summary>
    public void Save(string path)
    {
        var obj = new
        {
            paramModel = Model,
            paramQueryTimeoutSec = TimeoutSec,
            paramTtsVoice = Voice,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }
}
