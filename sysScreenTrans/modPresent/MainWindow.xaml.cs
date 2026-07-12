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

    /// <summary>功能列 Result 鈕按下（Issue #107）：本視窗僅發事件，喚回三態決策在 App 組合根。</summary>
    public event Action? ResultRequested;

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

        // 條目列數即時更新（#132）：頁內切夾/切日/增刪時，若該頁為當前分頁即更新底部狀態列。
        _notes.EntryCountChanged += n => { if (Host.Content == _notes) ShowEntryCount(n); };
        _history.EntryCountChanged += n => { if (Host.Content == _history) ShowEntryCount(n); };

        // 各分頁切換前先過「離開選項頁」守衛（#複查）：選項頁有未存變更時提示，取消則留在選項頁。
        // 切至筆記/歷史時於狀態列顯目前檢視條目數，其餘分頁隱藏（#132）。
        TabNotes.Checked += (_, _) => { if (!ConfirmLeaveOptions()) { ReselectOptionsTab(); return; } _notes.Reload(); Host.Content = _notes; ShowEntryCount(_notes.CurrentEntryCount); };
        TabHistory.Checked += (_, _) => { if (!ConfirmLeaveOptions()) { ReselectOptionsTab(); return; } _history.Reload(); Host.Content = _history; ShowEntryCount(_history.CurrentEntryCount); };
        TabContext.Checked += (_, _) => { if (!ConfirmLeaveOptions()) { ReselectOptionsTab(); return; } _context.Reload(); Host.Content = _context; ShowEntryCount(null); };
        TabOptions.Checked += (_, _) => { Host.Content = _options; ShowEntryCount(null); };
        TabAbout.Checked += (_, _) => { if (!ConfirmLeaveOptions()) { ReselectOptionsTab(); return; } Host.Content = _about; ShowEntryCount(null); };
        ResultBtn.Click += (_, _) => ResultRequested?.Invoke();

        Host.Content = _notes; // 預設筆記分頁（XAML IsChecked 於接線前已設，故此處明確帶入）
        ShowEntryCount(_notes.CurrentEntryCount); // #132：初始筆記分頁條目數
    }

    /// <summary>
    /// 離開選項頁守衛（#複查）：目前非選項頁或選項頁無未存變更時直接放行；否則提示——
    /// 「確定離開」＝還原變更值後放行，「取消」＝留在選項頁（回傳 false，由呼叫端還原分頁選取）。
    /// </summary>
    private bool ConfirmLeaveOptions()
    {
        if (Host.Content != _options || !_options.IsDirty)
        {
            return true;
        }
        // #125：兩選（OK/Cancel）改三選（Yes/No/Cancel）——存後離開／不存還原離開／取消留頁。
        var r = System.Windows.MessageBox.Show(
            "You have unsaved changes in Options.\n\n" +
            "Yes — save and leave\nNo — discard changes and leave\nCancel — stay on Options",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        switch (r)
        {
            case MessageBoxResult.Yes:
                return _options.TrySave(); // 存後離開：存檔成功才離開；失敗（TrySave 已報錯）留在選項頁
            case MessageBoxResult.No:
                _options.RevertChanges();  // 不存離開＝還原為上次儲存值
                return true;
            default:
                return false;              // Cancel＝留在選項頁
        }
    }

    /// <summary>取消離開後把分頁選取撥回選項頁（設 IsChecked 會觸發 TabOptions.Checked 還原 Host.Content）。</summary>
    private void ReselectOptionsTab() => TabOptions.IsChecked = true;

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

    private System.Windows.Threading.DispatcherTimer? _savedFlashTimer;

    /// <summary>設定儲存成功後於底部狀態列輕量閃示「Saved ✓」數秒（#125，取代原「Saved.」模態對話框）。</summary>
    public void FlashSaved()
    {
        SavedFlashText.Text = "Saved ✓";
        SavedFlashText.Visibility = Visibility.Visible;
        _savedFlashTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _savedFlashTimer.Stop();
        _savedFlashTimer.Tick -= OnSavedFlashElapsed; // 重按儲存時重置計時，不累加 handler
        _savedFlashTimer.Tick += OnSavedFlashElapsed;
        _savedFlashTimer.Start();
    }

    private void OnSavedFlashElapsed(object? sender, EventArgs e)
    {
        _savedFlashTimer?.Stop();
        SavedFlashText.Visibility = Visibility.Collapsed;
    }

    /// <summary>底部狀態列顯示目前檢視之條目列數（#132）；<paramref name="count"/> 為 null＝隱藏（非筆記/歷史分頁）。</summary>
    public void ShowEntryCount(int? count)
    {
        if (count is null)
        {
            CountText.Visibility = Visibility.Collapsed;
            CountSeparator.Visibility = Visibility.Collapsed;
            return;
        }
        CountText.Text = count.Value + (count.Value == 1 ? " entry" : " entries");
        CountText.Visibility = Visibility.Visible;
        CountSeparator.Visibility = Visibility.Visible;
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
