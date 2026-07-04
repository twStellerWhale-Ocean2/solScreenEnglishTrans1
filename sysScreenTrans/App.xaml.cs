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
/// 應用進入點：系統匣常駐（無主視窗），全域熱鍵 Alt+L 喚起 capture→query→present 主動線。
/// 對應 design ＜III.A＞ 單一 WPF exe、[setWi自訂Usr啟動結束常駐]。
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instanceGuard;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _keyStatusItem;
    private DockWindow? _dock;
    private HotKeyService? _hotkey;
    private ISpeechService? _speech;
    private AppConfig _config = new("gpt-4o-mini", 15, "");
    private bool _busy;
    private ResultWindow? _result; // 目前開啟中的結果視窗；下一次查詢取代前一個（失焦不再自動關閉）
    private readonly HistoryStore _historyStore = new(); // 查詢歷史本機儲存（spec#6）
    private HistoryWindow? _history; // 查詢歷史視窗（獨立、單一實例、非結果視窗）
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ScreenTrans-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 單一實例守衛：重複啟動偵測既有實例、明確提示並結束新實例，不重複註冊熱鍵
        // （design ＜setWi自訂Usr啟動結束常駐＞驗收 row02、＜II.C＞單一實例 invariant）。
        _instanceGuard = SingleInstanceGuard.Acquire();
        if (!_instanceGuard.IsFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "ScreenTrans 已在執行中（請見系統匣圖示）。",
                "ScreenTrans");
            Shutdown();
            return;
        }

        // 全域未捕捉例外：寫 log ＋ 顯示，避免靜默閃退
        DispatcherUnhandledException += OnUnhandled;

        _config = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        var keyReady = !string.IsNullOrWhiteSpace(apiKey);
        // TTS：Windows 內建語音（SAPI，離線免金鑰）；語音由設定選定（[techItem語音合成]）
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
        menu.Items.Add("開啟主控頁", null, (_, _) => OpenDock());
        menu.Items.Add("查詢歷史…", null, (_, _) => OpenHistory());
        menu.Items.Add("設定…", null, OnSettings);
        menu.Items.Add("關於 ScreenTrans", null, (_, _) => ShowAbout());
        menu.Items.Add("結束", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        // 雙擊系統匣圖示＝開啟主控頁
        _tray.DoubleClick += (_, _) => OpenDock();

        // 常駐主控頁（工作列按鈕型可見入口，spec#1）：啟動即建立、預設最小化不擋遊戲；
        // 關閉視窗＝收合非結束（DockWindow 攔截），與系統匣共用同一組維運動作。
        _dock = new DockWindow();
        _dock.RefreshStatus(keyReady, HotkeyDisplay());
        _dock.SettingsRequested += () => OnSettings(this, EventArgs.Empty);
        _dock.ExitRequested += ExitApp;
        _dock.HistoryRequested += OpenHistory;
        // 主控頁被帶到前景時（工作列按鈕／Alt+Tab／系統匣還原皆會 Activate）關閉 topmost 結果視窗，
        // 否則結果卡片會蓋住非 topmost 的主控頁，違反 spec#1「工作列可穩定尋得主控入口」。
        _dock.Activated += (_, _) => CloseResultBeforeMaintenanceUi();
        _dock.WindowState = WindowState.Minimized;
        _dock.Show();

        _hotkey = new HotKeyService();
        _hotkey.HotKeyPressed += OnHotKey;
        RegisterHotkeyOrWarn();
    }

    /// <summary>明確結束常駐：允許主控頁真正關閉後 Shutdown（關主控視窗本身只收合、不結束）。</summary>
    private void ExitApp()
    {
        _dock?.AllowClose();
        Shutdown();
    }

    /// <summary>以當前組態綁定註冊喚起快捷鍵；失敗（被占用／hook 安裝失敗）明確提示、程式仍常駐。</summary>
    private void RegisterHotkeyOrWarn()
    {
        if (_hotkey is null)
        {
            return;
        }
        if (!_hotkey.Register(HotKeyBinding.Parse(_config.Hotkey)))
        {
            System.Windows.MessageBox.Show(
                $"喚起快捷鍵「{HotkeyDisplay()}」註冊失敗（可能已被其他程式占用）。ScreenTrans 仍常駐，可於「設定」改用其他快捷鍵。",
                "ScreenTrans");
        }
    }

    private string HotkeyDisplay() => HotKeyBinding.Parse(_config.Hotkey).DisplayName;

    private string TrayText() => AppStatusText.TrayTip(HotkeyDisplay());

    /// <summary>從打包的 assets/app.ico 資源載入指定尺寸圖示；失敗退回系統預設。</summary>
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
            "發生錯誤（已記錄至 " + LogPath + "）：\n\n" + args.Exception.Message,
            "ScreenTrans 錯誤");
        args.Handled = true; // 攔下，避免整支程式閃退
    }

    /// <summary>Alt+L 喚起：遮罩選區截圖 → vision 查詢 → 結果視窗＋朗讀。</summary>
    private async void OnHotKey()
    {
        if (_busy)
        {
            return; // 一次一圈，避免重入
        }
        _busy = true;
        try
        {
            // 結果視窗失焦不再自動關閉：新查詢一開始即關閉前一結果視窗——須在遮罩截圖「之前」，
            // 否則前一結果視窗仍為 topmost，選區若與其重疊會把舊卡片截進畫面（同時亦維持至多一個）。
            _result?.Close();

            var mask = new MaskWindow();
            mask.ShowDialog();
            if (mask.Result is null)
            {
                return; // 取消或空選（以 Result 判定，不靠 DialogResult）
            }

            var win = new ResultWindow();
            _result = win;
            win.Closed += (_, _) => { if (ReferenceEquals(_result, win)) _result = null; };
            win.HistoryRequested += OpenHistory;
            win.ShowLoading();
            win.Show();
            win.Activate();

            try
            {
                var query = new QueryService(_config.Model, _config.TimeoutSec, _config.MaxRetries);
                var result = await query.QueryAsync(mask.Result.PngBytes);
                if (!result.IsEmpty)
                {
                    // 查詢成功即留存（新在前、截汰最舊）；即使該視窗已被取代仍記錄本次查詢
                    _historyStore.Append(result, _config.HistoryMax, DateTimeOffset.Now);
                    _history?.Reload(); // 歷史視窗開著則即時反映新紀錄
                }
                if (!ReferenceEquals(_result, win))
                {
                    return; // 載入中該視窗已被維運 UI 關閉或被新查詢取代：捨棄遲來結果，免對已關視窗填內容/幽靈朗讀
                }
                win.ShowResult(result, _speech!);
            }
            catch (QueryException ex)
            {
                if (!ReferenceEquals(_result, win))
                {
                    return; // 同上：視窗已不在，錯誤亦不再顯示於該視窗
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

    /// <summary>
    /// 開啟任一維運 UI（主控頁／設定／關於）前先關閉前一結果視窗。
    /// 結果視窗 <c>Topmost</c>，且移除失焦自動關閉後不再於維運視窗開啟時自動消失；
    /// 這些維運視窗無 owner／非 topmost，若不先關結果卡片會被蓋在其下而看不到、用不到
    /// （並還原本 issue 前「開維運 UI 即關結果」之時序）。設定路徑另需在 dispose 舊語音服務前關閉。
    /// </summary>
    private void CloseResultBeforeMaintenanceUi() => _result?.Close();

    /// <summary>系統匣「開啟主控頁」／雙擊圖示：先關結果視窗（免遮蔽）再還原主控頁。</summary>
    private void OpenDock()
    {
        CloseResultBeforeMaintenanceUi();
        _dock?.RestoreFromTray();
    }

    /// <summary>
    /// 開啟查詢歷史視窗（spec#6；結果視窗按鈕／常駐主控頁／系統匣皆可觸發）。
    /// 歷史為獨立視窗、單一實例；先關 topmost 結果卡片免遮蔽，再開啟或還原歷史視窗。
    /// </summary>
    private void OpenHistory()
    {
        CloseResultBeforeMaintenanceUi();
        if (_history is null)
        {
            _history = new HistoryWindow(_historyStore, () => _speech);
            _history.ViewRequested += ShowHistoryDetail;
            _history.Closed += (_, _) => _history = null;
            _history.Show();
        }
        else
        {
            _history.Reload();
            if (_history.WindowState == WindowState.Minimized)
            {
                _history.WindowState = WindowState.Normal;
            }
            _history.Show();
            _history.Activate();
        }
    }

    /// <summary>歷史「檢視」：以結果卡片顯示該筆三欄詳情（重用 ResultWindow 之整句/逐字發音組件）。</summary>
    private void ShowHistoryDetail(HistoryEntry entry)
    {
        var win = new ResultWindow();
        _result = win;
        win.Closed += (_, _) => { if (ReferenceEquals(_result, win)) _result = null; };
        win.HistoryRequested += OpenHistory;
        win.Show();
        win.Activate();
        win.ShowResult(entry.ToResult(), _speech!);
    }

    /// <summary>系統匣「關於」：先關結果視窗（免遮蔽）再顯示說明。</summary>
    private void ShowAbout()
    {
        CloseResultBeforeMaintenanceUi();
        System.Windows.MessageBox.Show(
            $"ScreenTrans\n按「{HotkeyDisplay()}」框選畫面英文，取得原文／KK 音標／繁中翻譯並可朗讀。\n（喚起快捷鍵可於「設定」自訂）",
            "關於 ScreenTrans");
    }

    /// <summary>系統匣「設定…」：開設定視窗，儲存後套用（重建語音服務、更新金鑰狀態列）。</summary>
    private void OnSettings(object? sender, EventArgs e)
    {
        // 開設定「之前」先關前一結果視窗：免其 topmost 卡片蓋住設定視窗，
        // 亦免存檔後 dispose 舊語音服務時該視窗續持已釋放服務（點播放/單字呼叫已釋放合成器）。
        CloseResultBeforeMaintenanceUi();

        var dlg = new SettingsWindow(_config);
        if (dlg.ShowDialog() != true)
        {
            return;
        }
        _config = dlg.ResultConfig;
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        (_speech as IDisposable)?.Dispose();
        _speech = new SpeechService(_config.Voice);
        RegisterHotkeyOrWarn(); // 快捷鍵可能已變更，重新註冊
        var keyReady = !string.IsNullOrWhiteSpace(apiKey);
        if (_tray is not null)
        {
            _tray.Text = TrayText();
        }
        if (_keyStatusItem is not null)
        {
            _keyStatusItem.Text = AppStatusText.KeyStatus(keyReady);
        }
        _dock?.RefreshStatus(keyReady, HotkeyDisplay()); // 主控頁與系統匣同步（單一來源）
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dock?.AllowClose(); // 確保結束流程中主控頁可真正關閉（不被收合攔截卡住）
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
