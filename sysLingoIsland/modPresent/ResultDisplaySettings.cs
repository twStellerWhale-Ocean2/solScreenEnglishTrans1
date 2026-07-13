namespace LingoIsland.Present;

/// <summary>
/// 查詢結果視窗之顯示偏好（#複查：選項頁「查詢視窗」可調）——英文原文基準字級，音標/中譯按此等比縮放。
/// 由 App 於啟動與設定儲存後自 <see cref="AppConfig"/> 同步（類比 <see cref="EntryDisplaySettings"/>），
/// <see cref="ResultWindow"/> 於重繪（Render）時讀取。持久化在 appsettings.json（AppConfig）。
/// </summary>
public static class ResultDisplaySettings
{
    /// <summary>英文原文基準字級（pt）；音標＝基準−4、中譯＝基準−2 等比縮放。</summary>
    public static double FontSize { get; set; } = AppConfig.DefaultResultFontSize;

    /// <summary>音標字級（基準−4，下限 12）。</summary>
    public static double PhoneticSize => System.Math.Max(12, FontSize - 4);

    /// <summary>中譯字級（基準−2，下限 12）。</summary>
    public static double TranslationSize => System.Math.Max(12, FontSize - 2);

    /// <summary>
    /// 查詢視窗失去焦點時是否自動隱藏（#複查）。預設 false＝維持 #105 行為（點主視窗不隱藏）；
    /// 勾選後點到其他視窗即自動隱藏、下次查詢再現。<see cref="ResultWindow"/> 於 Deactivated 時讀取。
    /// </summary>
    public static bool HideOnBlur { get; set; }

    /// <summary>自 AppConfig 同步（啟動與設定儲存後由 App 呼叫）。</summary>
    public static void SyncFrom(AppConfig c)
    {
        FontSize = c.ResultFontSize is >= 14 and <= 48 ? c.ResultFontSize : AppConfig.DefaultResultFontSize;
        HideOnBlur = c.ResultHideOnBlur;
    }
}
