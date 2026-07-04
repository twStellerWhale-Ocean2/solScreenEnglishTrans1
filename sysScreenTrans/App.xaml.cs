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
    private HotKeyService? _hotkey;
    private ISpeechService? _speech;
    private AppConfig _config = new("gpt-4o-mini", 15, "");
    private bool _busy;
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
            Text = "ScreenTrans — 遊戲畫面英文查詢（Alt+L）",
        };

        var menu = new WinForms.ContextMenuStrip();
        _keyStatusItem = new WinForms.ToolStripMenuItem(
            keyReady ? "● 金鑰已備妥（OPENAI_API_KEY）" : "○ 金鑰未設定（OPENAI_API_KEY）") { Enabled = false };
        menu.Items.Add(_keyStatusItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("設定…", null, OnSettings);
        menu.Items.Add("關於 ScreenTrans", null, (_, _) =>
            System.Windows.MessageBox.Show(
                "ScreenTrans\n按 Alt+L（左右皆可）框選畫面英文，取得原文／KK 音標／繁中翻譯並可朗讀。",
                "關於 ScreenTrans"));
        menu.Items.Add("結束", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;

        _hotkey = new HotKeyService();
        _hotkey.HotKeyPressed += OnHotKey;
        if (!_hotkey.Register())
        {
            System.Windows.MessageBox.Show(
                "熱鍵 Alt+L 註冊失敗（可能已被其他程式占用）。ScreenTrans 仍常駐，但無法以熱鍵喚起。",
                "ScreenTrans");
        }
    }

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
            var mask = new MaskWindow();
            mask.ShowDialog();
            if (mask.Result is null)
            {
                return; // 取消或空選（以 Result 判定，不靠 DialogResult）
            }

            var win = new ResultWindow();
            win.ShowLoading();
            win.Show();
            win.Activate();

            try
            {
                var query = new QueryService(_config.Model, _config.TimeoutSec);
                var result = await query.QueryAsync(mask.Result.PngBytes);
                win.ShowResult(result, _speech!);
            }
            catch (QueryException ex)
            {
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

    /// <summary>系統匣「設定…」：開設定視窗，儲存後套用（重建語音服務、更新金鑰狀態列）。</summary>
    private void OnSettings(object? sender, EventArgs e)
    {
        var dlg = new SettingsWindow(_config);
        if (dlg.ShowDialog() != true)
        {
            return;
        }
        _config = dlg.ResultConfig;
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        (_speech as IDisposable)?.Dispose();
        _speech = new SpeechService(_config.Voice);
        if (_keyStatusItem is not null)
        {
            _keyStatusItem.Text = string.IsNullOrWhiteSpace(apiKey)
                ? "○ 金鑰未設定（OPENAI_API_KEY）"
                : "● 金鑰已備妥（OPENAI_API_KEY）";
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
