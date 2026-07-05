namespace ScreenTrans.Present;

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
        $"ScreenTrans — English lookup for game screens ({hotkeyDisplay})";

    /// <summary>新版下載就緒（底部狀態列與關於分頁共用，Issue #51）。</summary>
    public static string UpdateReady(string version) => $"Update v{version} ready — restart to apply";

    /// <summary>新版就緒時之主視窗標題（OS 標題列＝工作列按鈕同步可見；USR 回饋）。</summary>
    public static string TitleUpdateReady(string version) => $"ScreenTrans — Update v{version} ready";

    /// <summary>手動檢查更新：已是最新（關於分頁）。</summary>
    public const string UpdateUpToDate = "You're up to date";

    /// <summary>手動檢查更新：進行中（關於分頁）。</summary>
    public const string UpdateChecking = "Checking for updates…";

    /// <summary>手動檢查更新：失敗（離線／來源不可達；不誤報「已是最新」）。</summary>
    public const string UpdateCheckFailed = "Couldn't check for updates. Check your connection and try again.";
}
