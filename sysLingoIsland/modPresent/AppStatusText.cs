namespace LingoIsland.Present;

/// <summary>
/// 維運狀態顯示文字之單一來源（Issue #25）：常駐主控頁與系統匣選單為同一組維運資訊之兩個入口鏡像，
/// 顯示字串一律取自本類、不各寫一份（design ＜II/III.C.(C)＞「動作來源單一」）。純函式、可單元測試。
/// </summary>
public static class AppStatusText
{
    /// <summary>金鑰狀態列（tray 選單與主控頁共用）。</summary>
    public static string KeyStatus(bool keyReady) =>
        keyReady ? "● Key ready (OPENAI_API_KEY)" : "○ Key not set (OPENAI_API_KEY)";

    /// <summary>主控頁的喚起快捷鍵列。</summary>
    public static string HotkeyLine(string hotkeyDisplay) => $"Hotkey: {hotkeyDisplay}";

    /// <summary>系統匣停留提示（滑鼠移到圖示上顯示）。</summary>
    public static string TrayTip(string hotkeyDisplay) =>
        $"LingoIsland — English lookup for game screens ({hotkeyDisplay})";

    /// <summary>新版下載就緒（底部狀態列與關於分頁共用，Issue #51）。</summary>
    public static string UpdateReady(string version) => $"Update v{version} ready — restart to apply";

    /// <summary>新版就緒時之主視窗標題（OS 標題列＝工作列按鈕同步可見；USR 回饋）。</summary>
    public static string TitleUpdateReady(string version) => $"LingoIsland — Update v{version} ready";

    /// <summary>手動檢查更新：已是最新（關於分頁）。</summary>
    public const string UpdateUpToDate = "You're up to date";

    /// <summary>手動檢查更新：確認中（關於分頁）。</summary>
    public const string UpdateChecking = "Checking for updates…";

    /// <summary>手動更新：下載中（#122：與「確認中」區分）。</summary>
    public const string UpdateDownloading = "Downloading update…";

    /// <summary>手動更新：下載中含進度（#122）。</summary>
    public static string UpdateDownloadingPercent(int percent) => $"Downloading update… {percent}%";

    // #122：更新失敗細分類——各給對應訊息與下一步，不再一律「檢查你的網路」。

    /// <summary>失敗：連不上更新伺服器（離線／DNS）。</summary>
    public const string UpdateFailedOffline = "Couldn't reach the update server. Please check your internet connection.";

    /// <summary>失敗：更新來源限流（GitHub API 403/429，查詢過於頻繁）。</summary>
    public const string UpdateFailedRateLimited = "Too many update checks right now. Please try again in a little while.";

    /// <summary>失敗：伺服器暫時性錯誤（5xx／逾時），重試後仍失敗。</summary>
    public const string UpdateFailedTransient = "The update server had a temporary problem. Please try again shortly.";

    /// <summary>失敗：更新來源異常（feed 解析／資產缺失／設定錯誤）。</summary>
    public const string UpdateFailedSource = "Couldn't read the update information. The update source may be unavailable.";

    /// <summary>失敗結果 → 對應訊息（#122）。</summary>
    public static string UpdateFailureMessage(UpdateCheckResult result) => result switch
    {
        UpdateCheckResult.FailedOffline => UpdateFailedOffline,
        UpdateCheckResult.FailedRateLimited => UpdateFailedRateLimited,
        UpdateCheckResult.FailedSource => UpdateFailedSource,
        _ => UpdateFailedTransient,
    };
}
