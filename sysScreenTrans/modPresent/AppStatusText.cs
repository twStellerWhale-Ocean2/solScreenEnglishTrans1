namespace ScreenTrans.Present;

/// <summary>
/// 維運狀態顯示文字之單一來源（Issue #25）：常駐主控頁與系統匣選單為同一組維運資訊之兩個入口鏡像，
/// 顯示字串一律取自本類、不各寫一份（design ＜II/III.C.(C)＞「動作來源單一」）。純函式、可單元測試。
/// </summary>
public static class AppStatusText
{
    /// <summary>金鑰狀態列（tray 選單與主控頁共用）。</summary>
    public static string KeyStatus(bool keyReady) =>
        keyReady ? "● 金鑰已備妥（OPENAI_API_KEY）" : "○ 金鑰未設定（OPENAI_API_KEY）";

    /// <summary>主控頁的喚起快捷鍵列。</summary>
    public static string HotkeyLine(string hotkeyDisplay) => $"喚起快捷鍵：{hotkeyDisplay}";

    /// <summary>系統匣停留提示（滑鼠移到圖示上顯示）。</summary>
    public static string TrayTip(string hotkeyDisplay) =>
        $"ScreenTrans — 遊戲畫面英文查詢（{hotkeyDisplay}）";

    /// <summary>新版下載就緒（底部狀態列與關於分頁共用，Issue #51）。</summary>
    public static string UpdateReady(string version) => $"新版 v{version} 已就緒，重新啟動後套用";

    /// <summary>新版就緒時之主視窗標題（OS 標題列＝工作列按鈕同步可見；USR 回饋）。</summary>
    public static string TitleUpdateReady(string version) => $"ScreenTrans — 新版 v{version} 已就緒";

    /// <summary>手動檢查更新：已是最新（關於分頁）。</summary>
    public const string UpdateUpToDate = "已是最新版本";

    /// <summary>手動檢查更新：進行中（關於分頁）。</summary>
    public const string UpdateChecking = "檢查更新中…";

    /// <summary>手動檢查更新：失敗（離線／來源不可達；不誤報「已是最新」）。</summary>
    public const string UpdateCheckFailed = "無法檢查更新，請確認網路後再試";
}
