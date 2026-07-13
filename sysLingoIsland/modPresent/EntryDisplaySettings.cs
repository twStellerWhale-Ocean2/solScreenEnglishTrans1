namespace LingoIsland.Present;

/// <summary>
/// 筆記/歷史條目原文之顯示偏好（#複查：選項頁「條目顯示」可調）——字級、粗體、自動換行。
/// 由 App 於啟動與設定儲存後自 <see cref="AppConfig"/> 同步（類比 <see cref="AutoAddSettings"/> 之靜態 holder），
/// NotesPage/HistoryPage 之 EntryRow 於重繪時讀取。持久化在 appsettings.json（AppConfig）。
/// </summary>
public static class EntryDisplaySettings
{
    /// <summary>原文字級（pt）；套用範圍限條目原文，不動時間小字/行尾鈕。</summary>
    public static double FontSize { get; set; } = AppConfig.DefaultEntryFontSize;

    /// <summary>原文是否粗體（SemiBold）。</summary>
    public static bool Bold { get; set; } = true;

    /// <summary>原文是否自動換行（true＝Wrap 完整顯示；false＝單行 CharacterEllipsis 省略）。</summary>
    public static bool Wrap { get; set; }

    /// <summary>條目卡底色透明度（百分比 0–100；v1.0.1：筆記/歷史共用、套為底色 alpha，取代原「過關卡透明」開關）。</summary>
    public static int CardOpacity { get; set; } = AppConfig.DefaultEntryCardOpacity;

    /// <summary>自 AppConfig 同步（啟動與設定儲存後由 App 呼叫）。</summary>
    public static void SyncFrom(AppConfig c)
    {
        FontSize = c.EntryFontSize is >= 8 and <= 48 ? c.EntryFontSize : AppConfig.DefaultEntryFontSize;
        Bold = c.EntryBold;
        Wrap = c.EntryWrap;
        CardOpacity = c.EntryCardOpacity is >= 0 and <= 100 ? c.EntryCardOpacity : AppConfig.DefaultEntryCardOpacity;
    }
}
