using System.Windows;
using CancelEventArgs = System.ComponentModel.CancelEventArgs;

namespace ScreenTrans.Present;

/// <summary>統一主視窗之分頁。</summary>
public enum MainTab { Notes, History, Context, Options, About }

/// <summary>
/// 統一 Office 式主視窗（Issue #34）：頂部功能列分頁（圖示＋文字）＋下方對應功能頁，取代原
/// DockWindow／HistoryWindow／NotesWindow／SettingsWindow 各獨立視窗。標準工作列視窗；
/// <b>關閉（✕）＝收合（最小化）而非結束</b>——唯呼叫端經「結束」才真正關閉；淺粉底＋logo 背景。
/// </summary>
public partial class MainWindow : Window
{
    private bool _exiting; // true 時允許真正關閉（結束程式）；否則關閉＝收合。結束由系統匣「結束」觸發。

    private readonly NotesPage _notes;
    private readonly HistoryPage _history;
    private readonly ContextPage _context;
    private readonly OptionsPage _options;
    private readonly AboutPage _about;

    public MainWindow(NotesPage notes, HistoryPage history, ContextPage context, OptionsPage options, AboutPage about)
    {
        InitializeComponent();
        _notes = notes;
        _history = history;
        _context = context;
        _options = options;
        _about = about;

        TabNotes.Checked += (_, _) => { _notes.Reload(); Host.Content = _notes; };
        TabHistory.Checked += (_, _) => { _history.Reload(); Host.Content = _history; };
        TabContext.Checked += (_, _) => { _context.Reload(); Host.Content = _context; };
        TabOptions.Checked += (_, _) => Host.Content = _options;
        TabAbout.Checked += (_, _) => Host.Content = _about;

        Host.Content = _notes; // 預設筆記分頁（XAML IsChecked 於接線前已設，故此處明確帶入）
    }

    /// <summary>切到指定分頁並自收合還原（tray／入口呼叫）。</summary>
    public void ShowTab(MainTab tab)
    {
        switch (tab)
        {
            case MainTab.Notes: TabNotes.IsChecked = true; break;
            case MainTab.History: TabHistory.IsChecked = true; break;
            case MainTab.Context: TabContext.IsChecked = true; break;
            case MainTab.Options: TabOptions.IsChecked = true; break;
            case MainTab.About: TabAbout.IsChecked = true; break;
        }
        RestoreFromTray();
    }

    /// <summary>更新底部狀態列之金鑰狀態與快捷鍵顯示（啟動與設定變更後呼叫；Issue #38 狀態置底）。</summary>
    public void RefreshStatus(bool keyReady, string hotkeyDisplay)
    {
        KeyStatusText.Text = AppStatusText.KeyStatus(keyReady);
        KeyStatusText.Foreground = keyReady ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.Firebrick;
        HotkeyText.Text = AppStatusText.HotkeyLine(hotkeyDisplay);
    }

    /// <summary>
    /// 新版已靜默下載就緒 → 底部狀態列顯示提示，並於 OS 標題列標示（工作列按鈕同步可見；
    /// Issue #51＋USR 回饋；重啟後套用、新進程標題自然回復）。
    /// </summary>
    public void ShowUpdateReady(string version)
    {
        Title = AppStatusText.TitleUpdateReady(version);
        UpdateText.Text = AppStatusText.UpdateReady(version);
        UpdateSeparator.Visibility = Visibility.Visible;
        UpdateText.Visibility = Visibility.Visible;
    }

    /// <summary>從收合狀態還原並帶到前景。</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>由呼叫端在「結束」流程中設定，令下一次 Close 真正關閉（而非收合）。</summary>
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
