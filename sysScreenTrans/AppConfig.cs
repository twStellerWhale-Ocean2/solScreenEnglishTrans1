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

    /// <summary>
    /// 設定檔正式路徑（Issue #51 遷居）：%APPDATA%\ScreenTrans\appsettings.json，與筆記/歷史/情境
    /// 三 store 同居——Velopack 更新會換置版本目錄，設定不得存 exe 旁（否則升級即失）。
    /// </summary>
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTrans", "appsettings.json");

    /// <summary>
    /// 路徑解析＋一次性遷移：appData 尚無設定檔而 exe 旁有舊檔（含發佈內附之預設檔）→ 複製過去；
    /// 之後一律讀寫 appData 路徑、exe 旁檔不再讀寫。遷移失敗不致命（Load 缺檔本就退預設）。
    /// </summary>
    public static string ResolveSettingsPath(string legacyPath, string appDataPath)
    {
        try
        {
            if (!File.Exists(appDataPath) && File.Exists(legacyPath))
            {
                var dir = Path.GetDirectoryName(appDataPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.Copy(legacyPath, appDataPath);
            }
        }
        catch
        {
            // 複製失敗（權限等）沿用 appData 路徑：Load 退預設、Save 會重建
        }
        return appDataPath;
    }

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

    /// <summary>寫回 appsettings.json（僅非機密參數；金鑰一律走環境變數、不落地）。目錄不存在時建立。</summary>
    public void Save(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
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
