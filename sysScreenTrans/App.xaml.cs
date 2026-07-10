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
    private HotkeyListenGuard? _listenGuard; // 指定快捷鍵監聽期間暫停/恢復全域熱鍵（Issue #89）
    private ISpeechService? _speech;
    private IPronunciationAssessor? _assessor;   // 發音評分（spec#10；金鑰於呼叫時讀、隨設定重建）
    private AppConfig _config = new("gpt-4o-mini", 15, "");
    private bool _busy;
    private ResultWindow? _result; // 目前開啟中的結果視窗；下一次查詢取代前一個
    private readonly HistoryStore _historyStore = new();
    private readonly NotesStore _notesStore = new();
    private readonly ContextStore _contextStore = new();
    private readonly INotificationService _notify = new WinToastNotificationService(); // 發音回饋系統通知（#101）
    private UpdateService? _updates;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ScreenTrans-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _instanceGuard = SingleInstanceGuard.Acquire();
        if (!_instanceGuard.IsFirstInstance)
        {
            System.Windows.MessageBox.Show("ScreenTrans is already running (see the tray icon).", "ScreenTrans");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;

        // 設定檔在 %APPDATA%（Issue #51 遷居：Velopack 更新換置版本目錄，存 exe 旁會失）；exe 旁舊檔一次性遷移
        _config = AppConfig.Load(AppConfig.ResolveSettingsPath(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"), AppConfig.SettingsPath));
        NoteDefaults.Load(); // 筆記加入預設（資料夾/底色/智能配色規則，Issue #55）
        EntryDisplaySettings.SyncFrom(_config); // #複查：條目顯示偏好（字級/粗體/換行）自 config 同步
        ResultDisplaySettings.SyncFrom(_config); // #複查：查詢結果視窗基準字級自 config 同步
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
        menu.Items.Add("Open Main Window", null, (_, _) => OpenMain(MainTab.Notes));
        menu.Items.Add("Result", null, (_, _) => SummonResult()); // 喚回結果卡（Issue #107：與主視窗 Result 鈕兩入口鏡像）
        menu.Items.Add("Query History", null, (_, _) => OpenMain(MainTab.History));
        menu.Items.Add("My Notes", null, (_, _) => OpenMain(MainTab.Notes));
        menu.Items.Add("Context", null, (_, _) => OpenMain(MainTab.Context));
        menu.Items.Add("Options", null, (_, _) => OpenMain(MainTab.Options));
        menu.Items.Add("About", null, (_, _) => OpenMain(MainTab.About));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenMain(MainTab.Notes);

        // 分頁（UserControl）＋統一主視窗
        _assessor = new PronunciationService(_config.PronModel, _config.TimeoutSec, _config.MaxRetries); // 發音評分（spec#10）
        _notesPage = new NotesPage(_notesStore, () => _speech,
            () => _assessor, () => new NaudioRecorder(), () => _config.PronPassThreshold, _notify);
        _notesPage.ViewRequested += entry => ShowDetail(entry.ToResult());
        _notesPage.EntryEditRequested += (id, text) => _ = EditNoteEntryAsync(id, text); // 複查回饋：筆記編輯→重譯
        _historyPage = new HistoryPage(_historyStore, () => _speech);
        _historyPage.ViewRequested += entry => ShowDetail(entry.ToResult());
        _historyPage.EntryEditRequested += (id, text) => _ = EditHistoryEntryAsync(id, text); // 複查回饋：歷史編輯→重譯
        _historyPage.AddToNotesRequested += entry => // 歷史「＋筆記」：套目前預設資料夾/底色（#55）
            AddToNotes(new NoteAddRequest(entry.ToResult(), NoteDefaults.FolderName, NoteDefaults.ColorHex));
        _optionsPage = new OptionsPage(_config);
        _optionsPage.SettingsChanged += ApplySettings;
        // 指定快捷鍵監聽期間暫停全域熱鍵、結束後依現行組態恢復（Issue #89）：
        // 避免監聽中按下現行鍵誤觸喚起，並使鍵盤組合不被 RegisterHotKey 攔截吞鍵而得正確擷取。
        _listenGuard = new HotkeyListenGuard(
            suspend: () => _hotkey?.Unregister(),
            resume: RegisterHotkeyOrWarn);
        _optionsPage.ListeningChanged += _listenGuard.OnListeningChanged;
        _contextStore.LoadMigrated(_config.Context); // #14 單一情境提示相容遷移為一則命名情境
        _contextPage = new ContextPage(_contextStore,
            bytes => new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries).DescribeImageAsync(bytes));

        _updates = new UpdateService();
        _updates.UpdateReady += v => Dispatcher.BeginInvoke(() => _main?.ShowUpdateReady(v));

        _main = new MainWindow(_notesPage, _historyPage, _contextPage, _optionsPage, new AboutPage(_updates));
        _main.RefreshStatus(keyReady, HotkeyDisplay());
        _main.ResultRequested += SummonResult; // 功能列 Result 鈕（Issue #107）
        // 主視窗取得焦點不關結果卡片（Issue #105：與主視窗共存，關閉時機僅限使用者關閉／新查詢或檢視取代／選項儲存重建）
        _main.WindowState = WindowState.Minimized;
        _main.Show();

        _hotkey = new HotKeyService();
        _hotkey.HotKeyPressed += OnHotKey;
        RegisterHotkeyOrWarn();

        // 啟動即背景檢查更新（Issue #51）：靜默下載、就緒才提示；未安裝形態/失敗皆靜默跳過
        _ = _updates.CheckAndDownloadAsync();
    }

    /// <summary>明確結束常駐：允許主視窗真正關閉後 Shutdown（關主視窗本身只收合、不結束）。</summary>
    private void ExitApp()
    {
        _main?.AllowClose();
        Shutdown();
    }

    private void RegisterHotkeyOrWarn()
    {
        var failed = new List<string>();
        if (_hotkey is not null && !_hotkey.Register(HotKeyBinding.Parse(_config.Hotkey)))
        {
            failed.Add(HotkeyDisplay());
        }
        if (failed.Count > 0)
        {
            System.Windows.MessageBox.Show(
                $"Failed to register hotkey(s) “{string.Join("”, “", failed)}” (they may be in use by another app). ScreenTrans keeps running; you can change the hotkeys in Options.",
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
            "An error occurred (logged to " + LogPath + "):\n\n" + args.Exception.Message, "ScreenTrans Error");
        args.Handled = true;
    }

    /// <summary>喚起（第一熱鍵）：遮罩選區/雙擊點選截圖 → vision 查詢 → 結果視窗＋朗讀＋歷史留存。</summary>
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
            await RunQueryAsync(mask.Result);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Lookup error:\n" + ex.Message, "ScreenTrans");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>查詢主動線（兩熱鍵共用）：結果視窗 loading → vision 查詢（依 <c>IsPointMode</c>）→ 結果/錯誤＋歷史留存。</summary>
    private async Task RunQueryAsync(CaptureResult capture)
    {
        var win = NewResultWindow();
        win.ShowLoading();
        win.Show();
        win.Activate();

        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries,
                _contextStore.ActiveText(), _contextStore.ActiveColorRules()); // 配色規則＝使用中情境各色描述（#69）
            var result = await query.QueryAsync(capture.PngBytes, capture.IsPointMode); // #54/#86 點選自動判斷
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

    /// <summary>建立並接線一個結果視窗（設為當前 _result；掛收藏入口，Issue #34 移除歷史/筆記入口）。
    /// 起手先過單一守衛——「同時至多一個」由本方法結構保證，不倚賴各呼叫端自律或遮罩覆蓋的偶然保護（#107 審查）。</summary>
    private ResultWindow NewResultWindow()
    {
        CloseResult();
        var win = new ResultWindow();
        _result = win;
        win.Closed += (_, _) => { if (ReferenceEquals(_result, win)) _result = null; };
        win.AddToNotesRequested += AddToNotes;
        win.WordQueryRequested += word => _ = LookupWordAsync(win, word); // 複查回饋：點單字＝查該字
        win.TextReQueryRequested += text => _ = ReTranslateAsync(win, text); // 複查回饋：編輯原文→重譯
        win.SetNoteTargets(TopFolderNames(), ActiveContextName()); // #55「加入至」下拉來源
        return win;
    }

    /// <summary>單字查詢（複查回饋：結果視窗點單字）：文字查該字義→推入該視窗導航堆疊（可往前返回原句）；失敗以 toast 明訊、不破壞現有結果。</summary>
    private async Task LookupWordAsync(ResultWindow win, string word)
    {
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryWordAsync(word);
            if (ReferenceEquals(_result, win)) // 視窗未被取代才回填
            {
                win.PushWordResult(result); // 內含結束等待游標
            }
            else
            {
                win.WordLookupFailed(); // 視窗已換：仍清該窗等待游標
            }
        }
        catch (QueryException ex)
        {
            win.WordLookupFailed(); // 清等待游標＋忙碌旗標
            ToastNotifier.Show("Word lookup failed: " + ex.Message);
        }
    }

    /// <summary>編輯筆記條目原文後重譯（複查回饋）：文字重查→更新該筆三欄（練習分數歸零）、存檔並重載筆記頁。空字串/失敗以 toast。</summary>
    private async Task EditNoteEntryAsync(string id, string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            _notesPage?.Reload();
            return;
        }
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryTextAsync(t);
            var data = _notesStore.LoadEnsured();
            if (NotesStore.UpdateEntryContent(data, id, result))
            {
                _notesStore.Save(data);
            }
        }
        catch (QueryException ex)
        {
            ToastNotifier.Show("Re-translate failed: " + ex.Message);
        }
        finally
        {
            _notesPage?.Reload(); // 成功以新內容重建、失敗還原原內容
        }
    }

    /// <summary>編輯歷史條目原文後重譯（複查回饋）：文字重查→更新該筆三欄、存檔並重載歷史頁。</summary>
    private async Task EditHistoryEntryAsync(string id, string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            _historyPage?.Reload();
            return;
        }
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryTextAsync(t);
            _historyStore.UpdateContent(id, result);
        }
        catch (QueryException ex)
        {
            ToastNotifier.Show("Re-translate failed: " + ex.Message);
        }
        finally
        {
            _historyPage?.Reload();
        }
    }

    /// <summary>編輯原文後重譯（複查回饋：辨識有誤時校正）：文字重查→取代該視窗目前結果；失敗 toast、清游標。</summary>
    private async Task ReTranslateAsync(ResultWindow win, string text)
    {
        try
        {
            var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
            var result = await query.QueryTextAsync(text);
            if (ReferenceEquals(_result, win))
            {
                win.ReplaceCurrentResult(result);
            }
            else
            {
                win.WordLookupFailed();
            }
        }
        catch (QueryException ex)
        {
            win.WordLookupFailed();
            ToastNotifier.Show("Re-translate failed: " + ex.Message);
        }
    }

    /// <summary>目前頂層資料夾名清單（供結果視窗「加入至」下拉，#55）。</summary>
    private List<string> TopFolderNames() => _notesStore.LoadEnsured().Folders.Select(f => f.Name).ToList();

    /// <summary>使用中情境名（空＝無使用中情境；供「加入至」預設夾解析與標籤，#55）。</summary>
    private string ActiveContextName() => ContextStore.GetActive(_contextStore.Load())?.Name ?? "";

    /// <summary>開啟統一主視窗並切到指定分頁（tray／入口）；結果卡片保留不關（Issue #105 與主視窗共存）。</summary>
    private void OpenMain(MainTab tab)
    {
        _main?.ShowTab(tab);
    }

    /// <summary>
    /// 加入我的筆記（去重）：結果視窗或自動加入觸發，加入至請求指定之資料夾並套底色（#55），右下角 toast 回饋（spec#7）。
    /// 資料夾名空 → 依使用中情境名（無情境則預設夾）解析。
    /// </summary>
    private void AddToNotes(NoteAddRequest req)
    {
        var folder = ResolveFolderName(req.FolderName);
        var msg = _notesStore.AddToNamedFolderAndSave(req.Result, folder, req.ColorHex, DateTimeOffset.Now) switch
        {
            NoteAddResult.Added => folder == NotesStore.DefaultFolderName ? "✓ Added to My Notes" : $"✓ Added to “{folder}”",
            NoteAddResult.AlreadyExists => "Already in Notes",
            _ => "Nothing to save",
        };
        ToastNotifier.Show(msg);
        _notesPage?.Reload();
    }

    /// <summary>解析目標資料夾名（#55）：非空即固定夾；空則使用中情境名、無情境則預設夾。</summary>
    private string ResolveFolderName(string chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return chosen.Trim();
        }
        var ctx = ActiveContextName();
        return ctx.Length > 0 ? ctx : NotesStore.DefaultFolderName;
    }

    /// <summary>「檢視」：以結果卡片顯示三欄詳情（重用 ResultWindow 之整句/逐字發音，供歷史與筆記共用）；
    /// 取代前一結果卡之單一守衛由 NewResultWindow 起手保證（Issue #105/#107）。</summary>
    private void ShowDetail(QueryResult r)
    {
        var win = NewResultWindow();
        win.Show();
        win.Activate();
        win.ShowResult(r, _speech!);
    }

    /// <summary>
    /// 喚回查詢結果視窗（Issue #107；主視窗 Result 鈕與 tray「Result」項兩入口鏡像）三態：
    /// 現有卡（含最小化中）→先還原再帶前景（不新開）；無卡且有查詢歷史→以最新一筆走「檢視」路徑重開（單一守衛）；
    /// 無任何歷史（含清除全部後、歷史檔毀損退空）→toast 提示、不開卡。喚回語意＝重開最新查詢、非最後顯示內容。
    /// </summary>
    private void SummonResult()
    {
        if (_result is not null)
        {
            if (_result.WindowState == WindowState.Minimized)
            {
                _result.WindowState = WindowState.Normal;
            }
            _result.Activate();
            return;
        }
        var latest = _historyStore.Load().FirstOrDefault();
        if (latest is null)
        {
            ToastNotifier.Show("No query result yet");
            return;
        }
        ShowDetail(latest.ToResult());
    }

    /// <summary>選項分頁儲存後套用：重建語音服務、重註冊熱鍵、更新狀態；關前一結果視窗（不續用已釋放服務）。</summary>
    private void ApplySettings(AppConfig cfg)
    {
        _config = cfg;
        CloseResult();
        (_speech as IDisposable)?.Dispose();
        _speech = new SpeechService(_config.Voice);
        _assessor = new PronunciationService(_config.PronModel, _config.TimeoutSec, _config.MaxRetries); // 隨模型/逾時重建（spec#10）
        EntryDisplaySettings.SyncFrom(_config); // #複查：條目字級/粗體/換行偏好同步後重建兩頁
        ResultDisplaySettings.SyncFrom(_config); // #複查：查詢視窗基準字級同步（下次查詢即套用；CloseResult 已關舊窗）
        _notesPage?.Reload(); // 門檻/條目顯示改動 → 重建卡片（intTest#36）
        _historyPage?.Reload(); // #複查：條目顯示改動同步套用歷史頁
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
        _updates?.ApplyOnExit(); // 新版已就緒者結束時掛起套用（下次啟動即新版；無則 no-op）
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
