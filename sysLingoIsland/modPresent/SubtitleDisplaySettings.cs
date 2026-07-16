namespace LingoIsland.Present;

/// <summary>
/// 影片頁字幕帶（當前句大字）之顯示偏好——字級、粗體（比照筆記「條目顯示」<see cref="EntryDisplaySettings"/>）。
/// 由 App 於啟動與設定儲存後自 <see cref="AppConfig"/> 同步；<see cref="VideoCapturePage"/> 於建立與設定變更時套用到 SubtitleBand。
/// 持久化在 appsettings.json（AppConfig 之 SubtitleFontSize／SubtitleBold）。
/// </summary>
public static class SubtitleDisplaySettings
{
    /// <summary>字幕帶字級（pt）。</summary>
    public static double FontSize { get; set; } = AppConfig.DefaultSubtitleFontSize;

    /// <summary>字幕帶是否粗體。</summary>
    public static bool Bold { get; set; }

    /// <summary>自 AppConfig 同步（啟動與設定儲存後由 App 呼叫）；字級界外回預設。</summary>
    public static void SyncFrom(AppConfig c)
    {
        FontSize = c.SubtitleFontSize is >= 12 and <= 48 ? c.SubtitleFontSize : AppConfig.DefaultSubtitleFontSize;
        Bold = c.SubtitleBold;
    }
}
