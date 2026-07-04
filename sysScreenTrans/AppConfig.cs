using System.IO;
using System.Text.Json;

namespace ScreenTrans;

/// <summary>appsettings.json 非機密偏好（[etyCfg自訂sysScreenTrans組態] D 類）。缺檔或欄位用預設。</summary>
/// <param name="MaxRetries">查詢暫時性錯誤最大重試次數（負值視為 0＝不重試）。</param>
public sealed record AppConfig(string Model, int TimeoutSec, string Voice, int MaxRetries = 2)
{
    /// <summary>查詢逾時秒數安全下限／預設（缺欄、解析失敗或非正值皆退回此值）。</summary>
    private const int DefaultTimeoutSec = 15;

    public static AppConfig Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var timeoutSec = r.TryGetProperty("paramQueryTimeoutSec", out var t) ? t.GetInt32() : DefaultTimeoutSec;
            return new AppConfig(
                r.TryGetProperty("paramModel", out var m) ? m.GetString() ?? "gpt-4o-mini" : "gpt-4o-mini",
                timeoutSec > 0 ? timeoutSec : DefaultTimeoutSec, // 非正值即刻取消會使查詢永遠逾時，套用安全下限
                r.TryGetProperty("paramTtsVoice", out var v) ? v.GetString() ?? "" : "",
                r.TryGetProperty("paramQueryMaxRetries", out var n) ? n.GetInt32() : 2);
        }
        catch
        {
            return new AppConfig("gpt-4o-mini", DefaultTimeoutSec, "");
        }
    }

    /// <summary>寫回 appsettings.json（僅非機密參數；金鑰一律走環境變數、不落地）。</summary>
    public void Save(string path)
    {
        var obj = new
        {
            paramModel = Model,
            paramQueryTimeoutSec = TimeoutSec,
            paramQueryMaxRetries = MaxRetries,
            paramTtsVoice = Voice,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }
}
