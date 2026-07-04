using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ScreenTrans.Capture;
using ScreenTrans.Present;
using ScreenTrans.Query;
using WinForms = System.Windows.Forms;

namespace ScreenTrans;

/// <summary>
/// 應用進入點：系統匣常駐（無主視窗自動啟動），全域喚起快捷鍵喚起 capture→query→present 主動線。
/// 維運/檢視整合於單一 Office 式主視窗 <see cref="MainWindow"/>（筆記／歷史／選項／關於分頁，Issue #34），
/// 取代原 DockWindow／HistoryWindow／NotesWindow／SettingsWindow。
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instanceGuard;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _keyStatusItem;
    private MainWindow? _main;
    private NotesPage? _notesPage;
    private HistoryPage? _historyPage;
    private ContextPage? _contextPage;
    private OptionsPage? _optionsPage;
    private HotKeyService? _hotkey;
    private ISpeechService? _speech;
    private AppConfig _config = new("gpt-4o-mini", 15, "");
    private bool _busy;
    private ResultWindow? _result; // 目前開啟中的結果視窗；下一次查詢取代前一個
    private readonly HistoryStore _historyStore = new();
    private readonly NotesStore _notesStore = new();
    private readonly ContextStore _contextStore = new();
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ScreenTrans-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _instanceGuard = SingleInstanceGuard.Acquire();
        if (!_instanceGuard.IsFirstInstance)
        {
            System.Windows.MessageBox.Show("ScreenTrans 已在執行中（請見系統匣圖示）。", "ScreenTrans");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;

        _config = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var keyReady = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        _speech = new SpeechService(_config.Voice);

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(WinForms.SystemInformation.SmallIconSize),
            Visible = true,
            Text = TrayText(),
        };
        var menu = new WinForms.ContextMenuStrip();
        _keyStatusItem = new WinForms.ToolStripMenuItem(AppStatusText.KeyStatus(keyReady)) { Enabled = false };
        menu.Items.Add(_keyStatusItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("開啟主視窗", null, (_, _) => OpenMain(MainTab.Notes));
        menu.Items.Add("查詢歷史", null, (_, _) => OpenMain(MainTab.History));
        menu.Items.Add("我的筆記", null, (_, _) => OpenMain(MainTab.Notes));
        menu.Items.Add("情境", null, (_, _) => OpenMain(MainTab.Context));
        menu.Items.Add("選項", null, (_, _) => OpenMain(MainTab.Options));
        menu.Items.Add("關於", null, (_, _) => OpenMain(MainTab.About));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenMain(MainTab.Notes);

        // 分頁（UserControl）＋統一主視窗
        _notesPage = new NotesPage(_notesStore, () => _speech);
        _notesPage.ViewRequested += entry => ShowDetail(entry.ToResult());
        _historyPage = new HistoryPage(_historyStore, () => _speech);
        _historyPage.ViewRequested += entry => ShowDetail(entry.ToResult());
        _historyPage.AddToNotesRequested += entry => AddToNotes(entry.ToResult());
        _optionsPage = new OptionsPage(_config);
        _optionsPage.SettingsChanged += ApplySettings;
        _contextStore.LoadMigrated(_config.Context); // #14 單一情境提示相容遷移為一則命名情境
        _contextPage = new ContextPage(_contextStore,
            bytes => new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries).DescribeImageAsync(bytes));

        _main = new MainWindow(_notesPage, _historyPage, _contextPage, _optionsPage, new AboutPage());
        _main.RefreshStatus(keyReady, HotkeyDisplay());
        // 主視窗被帶到前景時關 topmost 結果卡片（否則會蓋住主視窗）
        _main.Activated += (_, _) => CloseResult();
        _main.WindowState = WindowState.Minimized;
        _main.Show();

        _hotkey = new HotKeyService();
        _hotkey.HotKeyPressed += OnHotKey;
        RegisterHotkeyOrWarn();
    }

    /// <summary>明確結束常駐：允許主視窗真正關閉後 Shutdown（關主視窗本身只收合、不結束）。</summary>
    private void ExitApp()
    {
        _main?.AllowClose();
        Shutdown();
    }

    private void RegisterHotkeyOrWarn()
    {
        if (_hotkey is null)
        {
            return;
        }
        if (!_hotkey.Register(HotKeyBinding.Parse(_config.Hotkey)))
        {
            System.Windows.MessageBox.Show(
                $"喚起快捷鍵「{HotkeyDisplay()}」註冊失敗（可能已被其他程式占用）。ScreenTrans 仍常駐，可於「選項」改用其他快捷鍵。",
                "ScreenTrans");
        }
    }

    private string HotkeyDisplay() => HotKeyBinding.Parse(_config.Hotkey).DisplayName;

    private string TrayText() => AppStatusText.TrayTip(HotkeyDisplay());

    private static Icon LoadAppIcon(System.Drawing.Size size)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/assets/app.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                return new Icon(stream, size);
            }
        }
        catch { /* 資源缺失退回預設 */ }
        return SystemIcons.Application;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        try { File.WriteAllText(LogPath, DateTime.Now + "\n" + args.Exception); }
        catch { /* log 寫入失敗不致命 */ }
        System.Windows.MessageBox.Show(
            "發生錯誤（已記錄至 " + LogPath + "）：\n\n" + args.Exception.Message, "ScreenTrans 錯誤");
        args.Handled = true;
    }

    /// <summary>喚起：遮罩選區截圖 → vision 查詢 → 結果視窗＋朗讀＋歷史留存。</summary>
    private async void OnHotKey()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            CloseResult(); // 新查詢一開始即關前一結果視窗（守衛，Issue #32）

            var mask = new MaskWindow();
            mask.ShowDialog();
            if (mask.Result is null)
            {
                return;
            }

            var win = NewResultWindow();
            win.ShowLoading();
            win.Show();
            win.Activate();

            try
            {
                var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries, _contextStore.ActiveText());
                var result = await query.QueryAsync(mask.Result.PngBytes);
                if (!result.IsEmpty)
                {
                    _historyStore.Append(result, _config.HistoryMax, DateTimeOffset.Now);
                    _historyPage?.Reload();
                }
                if (!ReferenceEquals(_result, win))
                {
                    return;
                }
                win.ShowResult(result, _speech!);
            }
            catch (QueryException ex)
            {
                if (!ReferenceEquals(_result, win))
                {
                    return;
                }
                win.ShowError(ex.Message);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("喚起流程錯誤：\n" + ex.Message, "ScreenTrans");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>建立並接線一個結果視窗（設為當前 _result；掛收藏入口，Issue #34 移除歷史/筆記入口）。</summary>
    private ResultWindow NewResultWindow()
    {
        var win = new ResultWindow();
        _result = win;
        win.Closed += (_, _) => { if (ReferenceEquals(_result, win)) _result = null; };
        win.AddToNotesRequested += AddToNotes;
        return win;
    }

    /// <summary>開啟統一主視窗並切到指定分頁（tray／入口）；先關 topmost 結果卡片免遮蔽。</summary>
    private void OpenMain(MainTab tab)
    {
        CloseResult();
        _main?.ShowTab(tab);
    }

    /// <summary>加入我的筆記（去重）：結果視窗、自動加入或歷史條目觸發，右下角 toast 回饋（spec#7）。</summary>
    private void AddToNotes(QueryResult r)
    {
        var msg = _notesStore.AddAndSave(r, DateTimeOffset.Now) switch
        {
            NoteAddResult.Added => "✓ 已加入我的筆記",
            NoteAddResult.AlreadyExists => "已在筆記中",
            _ => "無可收藏內容",
        };
        ToastNotifier.Show(msg);
        _notesPage?.Reload();
    }

    /// <summary>「檢視」：以結果卡片顯示三欄詳情（重用 ResultWindow 之整句/逐字發音，供歷史與筆記共用）。</summary>
    private void ShowDetail(QueryResult r)
    {
        var win = NewResultWindow();
        win.Show();
        win.Activate();
        win.ShowResult(r, _speech!);
    }

    /// <summary>選項分頁儲存後套用：重建語音服務、重註冊熱鍵、更新狀態；關前一結果視窗（不續用已釋放服務）。</summary>
    private void ApplySettings(AppConfig cfg)
    {
        _config = cfg;
        CloseResult();
        (_speech as IDisposable)?.Dispose();
        _speech = new SpeechService(_config.Voice);
        RegisterHotkeyOrWarn();
        var keyReady = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        if (_tray is not null)
        {
            _tray.Text = TrayText();
        }
        if (_keyStatusItem is not null)
        {
            _keyStatusItem.Text = AppStatusText.KeyStatus(keyReady);
        }
        _main?.RefreshStatus(keyReady, HotkeyDisplay());
    }

    /// <summary>
    /// 關閉結果視窗之單一守衛（Issue #32）：先解 <c>_result</c> 參考再關閉，僅在未進入關閉序列時
    /// 才 <c>Close()</c>；極端交錯以 catch 兜底，避免「Cannot call Close while a Window is closing」重入崩潰。
    /// </summary>
    private void CloseResult()
    {
        var r = _result;
        if (r is null)
        {
            return;
        }
        _result = null;
        if (r.IsClosing)
        {
            return;
        }
        try { r.Close(); }
        catch (InvalidOperationException) { /* 已在關閉序列中，忽略 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.AllowClose();
        _hotkey?.Dispose();
        (_speech as IDisposable)?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}
