using ScreenTrans.Present;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// 維運狀態顯示文字單一來源（Issue #25）：常駐主控頁與系統匣共用同一組字串，
/// 於此鎖定金鑰狀態／快捷鍵／tray 提示之呈現，確保兩入口鏡像一致、不各寫一份。
/// </summary>
public class AppStatusTextTests
{
    [Fact]
    public void KeyStatus_Ready_ShowsFilledMark()
    {
        Assert.Equal("● Key ready (OPENAI_API_KEY)", AppStatusText.KeyStatus(true));
    }

    [Fact]
    public void KeyStatus_NotReady_ShowsHollowMark()
    {
        Assert.Equal("○ Key not set (OPENAI_API_KEY)", AppStatusText.KeyStatus(false));
    }

    [Fact]
    public void HotkeyLine_EmbedsDisplayName()
    {
        Assert.Equal("Hotkey: Alt + L", AppStatusText.HotkeyLine("Alt + L"));
    }

    [Fact]
    public void TrayTip_EmbedsHotkey()
    {
        Assert.Equal("ScreenTrans — English lookup for game screens (Ctrl + Shift + F)",
            AppStatusText.TrayTip("Ctrl + Shift + F"));
    }

    [Fact]
    public void UpdateReady_EmbedsVersion()
    {
        // Issue #51：底部狀態列與關於分頁共用同一字串（單源）
        Assert.Equal("Update v0.15.1 ready — restart to apply", AppStatusText.UpdateReady("0.15.1"));
    }

    [Fact]
    public void TitleUpdateReady_EmbedsVersion_AfterAppName()
    {
        // USR 回饋：主視窗標題（OS 標題列＝工作列按鈕）於「ScreenTrans」後標示新版就緒
        Assert.Equal("ScreenTrans — Update v0.15.2 ready", AppStatusText.TitleUpdateReady("0.15.2"));
    }

    [Fact]
    public void UpdateCheckStrings_DistinguishFailureFromUpToDate()
    {
        // 手動檢查失敗（離線）不得與「已是最新版本」同文——不誤報最新（Issue #51）
        Assert.Equal("You're up to date", AppStatusText.UpdateUpToDate);
        Assert.Equal("Couldn't check for updates. Check your connection and try again.", AppStatusText.UpdateCheckFailed);
        Assert.NotEqual(AppStatusText.UpdateUpToDate, AppStatusText.UpdateCheckFailed);
    }
}
