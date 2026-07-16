using System.IO;
using System.Text.Json;

namespace LingoIsland;

/// <summary>appsettings.json 非機密偏好（[etyCfg自訂sysLingoIsland組態] D 類）。缺檔或欄位用預設。</summary>
/// <param name="MaxRetries">查詢暫時性錯誤最大重試次數（負值視為 0＝不重試）。</param>
/// <param name="Hotkey">喚起快捷鍵綁定（序列化字串，如 <c>Alt+L</c>／<c>Ctrl+Shift+F</c>／<c>Mouse:Middle</c>）。</param>
/// <param name="HistoryMax">查詢歷史保留筆數上限（非正值套用預設 200）。</param>
/// <param name="Context">應用情境提示（自然語言，選填；非空時查詢注入為參考情境，spec#8）。</param>
public sealed record AppConfig(string Model, int TimeoutSec, string Voice, int MaxRetries = 2, string Hotkey = "Alt+L", int HistoryMax = 200, string Context = "", int PronPassThreshold = 80, string PronModel = "gpt-audio-1.5", double EntryFontSize = 18, bool EntryBold = true, bool EntryWrap = false, double ResultFontSize = 28, bool ResultHideOnBlur = false, int EntryCardOpacity = 40, double SubtitleFontSize = 16, bool SubtitleBold = false, double SearchThumbHeight = 36)
{
    /// <summary>影片搜尋結果縮圖高度預設（px；選項頁可調 28–120；寬＝高×16/9）。</summary>
    public const double DefaultSearchThumbHeight = 36;

    /// <summary>筆記/歷史條目原文字級預設（#複查：選項頁「條目顯示」可調；缺欄或超界回此值）。</summary>
    public const double DefaultEntryFontSize = 18;

    /// <summary>條目卡底色透明度預設（百分比 0–100；v1.0.1：筆記/歷史共用，40≈原 #66FFFFFF 半透明白）。</summary>
    public const int DefaultEntryCardOpacity = 40;

    /// <summary>查詢結果視窗英文原文基準字級預設（#複查：音標/中譯按此等比縮放；缺欄或超界回此值）。</summary>
    public const double DefaultResultFontSize = 28;

    /// <summary>影片頁字幕帶（當前句大字）字級預設（選項頁「Video subtitle」可調，比照筆記；缺欄或超界回此值）。</summary>
    public const double DefaultSubtitleFontSize = 16;

    /// <summary>查詢逾時秒數安全下限／預設（缺欄、解析失敗或非正值皆退回此值）。</summary>
    private const int DefaultTimeoutSec = 15;

    /// <summary>喚起快捷鍵預設綁定（沿用原硬編碼 Alt+L）。</summary>
    public const string DefaultHotkey = "Alt+L";

    /// <summary>查詢歷史保留筆數預設／下限（缺欄或非正值皆退回此值）。</summary>
    public const int DefaultHistoryMax = 200;

    /// <summary>發音練習及格門檻預設（0–100；spec#10）。</summary>
    public const int DefaultPronThreshold = 80;

    /// <summary>發音評分模型預設（須支援音訊輸入；spec#10）。</summary>
    public const string DefaultPronModel = "gpt-audio-1.5";

    /// <summary>
    /// 設定檔正式路徑（Issue #51 遷居）：%APPDATA%\LingoIsland\appsettings.json，與筆記/歷史/情境
    /// 三 store 同居——Velopack 更新會換置版本目錄，設定不得存 exe 旁（否則升級即失）。
    /// </summary>
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland", "appsettings.json");

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
            var pronThreshold = r.TryGetProperty("paramPronPassThreshold", out var pt) ? pt.GetInt32() : DefaultPronThreshold;
            var entryFont = r.TryGetProperty("paramEntryFontSize", out var ef) && ef.TryGetDouble(out var efv) ? efv : DefaultEntryFontSize;
            var resultFont = r.TryGetProperty("paramResultFontSize", out var rf) && rf.TryGetDouble(out var rfv) ? rfv : DefaultResultFontSize;
            return new AppConfig(
                r.TryGetProperty("paramModel", out var m) ? m.GetString() ?? "gpt-4o-mini" : "gpt-4o-mini",
                timeoutSec > 0 ? timeoutSec : DefaultTimeoutSec, // 非正值即刻取消會使查詢永遠逾時，套用安全下限
                r.TryGetProperty("paramTtsVoice", out var v) ? v.GetString() ?? "" : "",
                r.TryGetProperty("paramQueryMaxRetries", out var n) ? n.GetInt32() : 2,
                r.TryGetProperty("paramHotkey", out var h) ? h.GetString() ?? DefaultHotkey : DefaultHotkey,
                historyMax > 0 ? historyMax : DefaultHistoryMax, // 非正上限套用預設，免歷史被清空或無界成長
                r.TryGetProperty("paramContextHint", out var cx) ? cx.GetString() ?? "" : "", // 應用情境提示（選填）；Issue #90 起舊 paramHotkeyPoint 忽略不讀
                pronThreshold is >= 0 and <= 100 ? pronThreshold : DefaultPronThreshold, // 發音及格門檻（spec#10；界外套預設）
                NormalizePronModel(r.TryGetProperty("paramPronModel", out var pm) ? pm.GetString() : null), // 發音評分模型（spec#10）
                entryFont, // 條目原文字級（#複查；holder 端另鉗界）
                r.TryGetProperty("paramEntryBold", out var eb) && eb.ValueKind == JsonValueKind.False ? false : true, // 條目粗體（缺欄預設 true）
                r.TryGetProperty("paramEntryWrap", out var ew) && ew.ValueKind == JsonValueKind.True, // 條目自動換行（缺欄預設 false）
                resultFont, // 查詢結果視窗基準字級（#複查）
                r.TryGetProperty("paramResultHideOnBlur", out var hb) && hb.ValueKind == JsonValueKind.True, // 查詢視窗失焦自動隱藏（缺欄預設 false，維持 #105）
                r.TryGetProperty("paramEntryCardOpacity", out var co) && co.TryGetInt32(out var cov) && cov is >= 0 and <= 100 ? cov : DefaultEntryCardOpacity, // v1.0.1：條目卡底色透明度（0–100；缺欄/界外回預設 40）
                r.TryGetProperty("paramSubtitleFontSize", out var sf) && sf.TryGetDouble(out var sfv) ? sfv : DefaultSubtitleFontSize, // 影片頁字幕帶字級（holder 端另鉗界）
                r.TryGetProperty("paramSubtitleBold", out var sbold) && sbold.ValueKind == JsonValueKind.True, // 影片頁字幕帶粗體（缺欄預設 false）
                r.TryGetProperty("paramSearchThumbHeight", out var sth) && sth.TryGetDouble(out var sthv) ? sthv : DefaultSearchThumbHeight); // 影片搜尋縮圖高度（頁端另鉗界）
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
            paramPronPassThreshold = PronPassThreshold,
            paramPronModel = PronModel,
            paramEntryFontSize = EntryFontSize, // #複查：條目顯示偏好持久化
            paramEntryBold = EntryBold,
            paramEntryWrap = EntryWrap,
            paramResultFontSize = ResultFontSize, // #複查：查詢結果視窗基準字級
            paramResultHideOnBlur = ResultHideOnBlur, // #複查：查詢視窗失焦自動隱藏
            paramEntryCardOpacity = EntryCardOpacity, // v1.0.1：條目卡底色透明度（0–100）
            paramSubtitleFontSize = SubtitleFontSize, // 影片頁字幕帶顯示偏好（比照筆記）
            paramSubtitleBold = SubtitleBold,
            paramSearchThumbHeight = SearchThumbHeight, // 影片搜尋結果縮圖高度（#複查：選項頁可調、需持久化）
        };
        File.WriteAllText(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>發音模型讀取邊界：舊預設 gpt-audio-mini 未在目前官方音訊指南列為範例模型，遷移至官方音訊模型。</summary>
    public static string NormalizePronModel(string? model)
    {
        var m = (model ?? "").Trim();
        return string.IsNullOrWhiteSpace(m) || string.Equals(m, "gpt-audio-mini", StringComparison.OrdinalIgnoreCase)
            ? DefaultPronModel
            : m;
    }
}
