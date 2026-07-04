using System.Windows;
using CancelEventArgs = System.ComponentModel.CancelEventArgs;

namespace ScreenTrans.Present;

/// <summary>
/// 常駐主控頁（Issue #25，design ＜III.C.(C)＞）：工作列按鈕型可見主控入口，承載金鑰狀態、
/// 喚起快捷鍵、設定、結束。<b>關閉（✕）＝收合（最小化）而非結束程式</b>——唯呼叫端經「結束」
/// 才真正 <see cref="Window.Close"/>／Shutdown；`ShowInTaskbar` 使其常顯於工作列、可 Alt+Tab 尋得，
/// 不依賴 Windows 系統匣顯示設定。維運顯示文字取自 <see cref="AppStatusText"/>（與系統匣共用單一來源）。
/// </summary>
public partial class DockWindow : Window
{
    private bool _exiting; // true 時允許真正關閉（結束程式）；否則關閉＝收合

    /// <summary>按「設定…」時觸發（呼叫端開設定視窗）。</summary>
    public event Action? SettingsRequested;

    /// <summary>按「結束」時觸發（呼叫端結束整個常駐程式）。</summary>
    public event Action? ExitRequested;

    /// <summary>按「查詢歷史」時觸發（呼叫端開查詢歷史視窗，spec#6）。</summary>
    public event Action? HistoryRequested;

    public DockWindow()
    {
        InitializeComponent();
        SettingsBtn.Click += (_, _) => SettingsRequested?.Invoke();
        ExitBtn.Click += (_, _) => ExitRequested?.Invoke();
        HistoryBtn.Click += (_, _) => HistoryRequested?.Invoke();
    }

    /// <summary>更新金鑰狀態與快捷鍵顯示（啟動與設定變更後呼叫）。</summary>
    public void RefreshStatus(bool keyReady, string hotkeyDisplay)
    {
        KeyStatusText.Text = AppStatusText.KeyStatus(keyReady);
        HotkeyText.Text = AppStatusText.HotkeyLine(hotkeyDisplay);
    }

    /// <summary>從收合狀態還原並帶到前景（點工作列／系統匣「開啟主控頁」時呼叫）。</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>由呼叫端在「結束」流程中設定，令下一次 Close 真正關閉視窗（而非收合）。</summary>
    public void AllowClose() => _exiting = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;          // 關閉＝收合，程式續常駐
            WindowState = WindowState.Minimized;
            return;
        }
        base.OnClosing(e);
    }
}
