using System.IO;
using System.Text.Json;

namespace ScreenTrans;

/// <summary>appsettings.json 非機密偏好（[etyCfg自訂sysScreenTrans組態] D 類）。缺檔或欄位用預設。</summary>
/// <param name="MaxRetries">查詢暫時性錯誤最大重試次數（負值視為 0＝不重試）。</param>
/// <param name="Hotkey">喚起快捷鍵綁定（序列化字串，如 <c>Alt+L</c>／<c>Ctrl+Shift+F</c>／<c>Mouse:Middle</c>）。</param>
/// <param name="HistoryMax">查詢歷史保留筆數上限（非正值套用預設 200）。</param>
/// <param name="Context">應用情境提示（自然語言，選填；非空時查詢注入為參考情境，spec#8）。</param>
public sealed record AppConfig(string Model, int TimeoutSec, string Voice, int MaxRetries = 2, string Hotkey = "Alt+L", int HistoryMax = 200, string Context = "")
{
    /// <summary>查詢逾時秒數安全下限／預設（缺欄、解析失敗或非正值皆退回此值）。</summary>
    private const int DefaultTimeoutSec = 15;

    /// <summary>喚起快捷鍵預設綁定（沿用原硬編碼 Alt+L）。</summary>
    public const string DefaultHotkey = "Alt+L";

    /// <summary>查詢歷史保留筆數預設／下限（缺欄或非正值皆退回此值）。</summary>
    public const int DefaultHistoryMax = 200;

    public static AppConfig Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var timeoutSec = r.TryGetProperty("paramQueryTimeoutSec", out var t) ? t.GetInt32() : DefaultTimeoutSec;
            var historyMax = r.TryGetProperty("paramHistoryMax", out var hm) ? hm.GetInt32() : DefaultHistoryMax;
            return new AppConfig(
                r.TryGetProperty("paramModel", out var m) ? m.GetString() ?? "gpt-4o-mini" : "gpt-4o-mini",
                timeoutSec > 0 ? timeoutSec : DefaultTimeoutSec, // 非正值即刻取消會使查詢永遠逾時，套用安全下限
                r.TryGetProperty("paramTtsVoice", out var v) ? v.GetString() ?? "" : "",
                r.TryGetProperty("paramQueryMaxRetries", out var n) ? n.GetInt32() : 2,
                r.TryGetProperty("paramHotkey", out var h) ? h.GetString() ?? DefaultHotkey : DefaultHotkey,
                historyMax > 0 ? historyMax : DefaultHistoryMax, // 非正上限套用預設，免歷史被清空或無界成長
                r.TryGetProperty("paramContextHint", out var cx) ? cx.GetString() ?? "" : ""); // 應用情境提示（選填）
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
            paramHotkey = Hotkey,
            paramHistoryMax = HistoryMax,
            paramContextHint = Context,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }
}
