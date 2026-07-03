using System.Drawing;
using System.Windows;
using ScreenTrans.Capture;
using WinForms = System.Windows.Forms;

namespace ScreenTrans;

/// <summary>
/// 應用進入點：系統匣常駐（無主視窗），全域熱鍵 Alt+L 喚起查詢主動線。
/// 對應 design ＜III.A＞ 單一 WPF exe、[setWi自訂Usr啟動結束常駐]、[modCapture模組]。
/// </summary>
public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _tray;
    private HotKeyService? _hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 無主視窗常駐：僅在明確 Shutdown()（tray「結束」）時退出。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _tray = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "ScreenTrans — 遊戲畫面英文查詢（Alt+L）",
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("關於 ScreenTrans", null, (_, _) =>
            System.Windows.MessageBox.Show(
                "ScreenTrans\n按 Alt+L（左右皆可）框選畫面英文，取得原文／KK 音標／繁中翻譯並可朗讀。",
                "關於 ScreenTrans"));
        menu.Items.Add(new WinForms.ToolStripSeparator());
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

    /// <summary>Alt+L 喚起：capture→query→present 主動線入口。</summary>
    private void OnHotKey()
    {
        // 第一片佔位：後續切片以「遮罩選區截圖 → vision 查詢 → 結果視窗」取代。
        System.Windows.MessageBox.Show("Alt+L 已喚起（遮罩選區截圖即將接入）", "ScreenTrans");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }
}
