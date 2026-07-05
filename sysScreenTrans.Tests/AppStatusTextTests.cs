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
        Assert.Equal("● 金鑰已備妥（OPENAI_API_KEY）", AppStatusText.KeyStatus(true));
    }

    [Fact]
    public void KeyStatus_NotReady_ShowsHollowMark()
    {
        Assert.Equal("○ 金鑰未設定（OPENAI_API_KEY）", AppStatusText.KeyStatus(false));
    }

    [Fact]
    public void HotkeyLine_EmbedsDisplayName()
    {
        Assert.Equal("喚起快捷鍵：Alt + L", AppStatusText.HotkeyLine("Alt + L"));
    }

    [Fact]
    public void TrayTip_EmbedsHotkey()
    {
        Assert.Equal("ScreenTrans — 遊戲畫面英文查詢（Ctrl + Shift + F）",
            AppStatusText.TrayTip("Ctrl + Shift + F"));
    }

    [Fact]
    public void UpdateReady_EmbedsVersion()
    {
        // Issue #51：底部狀態列與關於分頁共用同一字串（單源）
        Assert.Equal("新版 v0.15.1 已就緒，重新啟動後套用", AppStatusText.UpdateReady("0.15.1"));
    }

    [Fact]
    public void TitleUpdateReady_EmbedsVersion_AfterAppName()
    {
        // USR 回饋：主視窗標題（OS 標題列＝工作列按鈕）於「ScreenTrans」後標示新版就緒
        Assert.Equal("ScreenTrans — 新版 v0.15.2 已就緒", AppStatusText.TitleUpdateReady("0.15.2"));
    }

    [Fact]
    public void UpdateCheckStrings_DistinguishFailureFromUpToDate()
    {
        // 手動檢查失敗（離線）不得與「已是最新版本」同文——不誤報最新（Issue #51）
        Assert.Equal("已是最新版本", AppStatusText.UpdateUpToDate);
        Assert.Equal("無法檢查更新，請確認網路後再試", AppStatusText.UpdateCheckFailed);
        Assert.NotEqual(AppStatusText.UpdateUpToDate, AppStatusText.UpdateCheckFailed);
    }
}
