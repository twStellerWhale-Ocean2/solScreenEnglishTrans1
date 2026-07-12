using System.Windows;
using CancelEventArgs = System.ComponentModel.CancelEventArgs;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ScreenTrans.Present;

/// <summary>
/// 獨立字典視窗（v1.0.1，USR 回饋）：查詢結果／查字典改回**獨立視窗**（取代 #135 併入主視窗之 Dictionary 分頁）——
/// 避免「於筆記頁檢視單字時整個主視窗跳到 Dictionary 分頁、打斷發音練習節奏」。內容為共用 <see cref="DictionaryPage"/>
/// （頂部可編輯下拉打字查詢＋查詢歷史、下方 ResultView 三欄結果）。Topmost 浮於遊戲上、與主視窗共存；
/// 關閉（✕／ESC）＝**隱藏**而非銷毀（保留狀態、可再喚出）；記住位置大小（<see cref="UiStateStore"/>）。
/// </summary>
public partial class DictionaryWindow : Window
{
    private readonly UiStateStore _ui;
    private bool _allowClose; // true 時允許真正關閉（結束程式）；否則關閉＝隱藏。

    public DictionaryWindow()
    {
        InitializeComponent();
        _ui = UiStateStore.Load();
        ApplyBounds();
    }

    /// <summary>顯示並帶到前景（喚起/檢視/喚回共用）：隱藏中先 Show、最小化先還原、置頂帶焦點。</summary>
    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Topmost = true; // 疊於無邊框遊戲之上（與主視窗共存）
        Activate();
    }

    /// <summary>由 App 於「結束」流程呼叫，令下一次 Close 真正關閉（而非隱藏）。</summary>
    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveBounds();
        if (!_allowClose)
        {
            e.Cancel = true; // 關閉＝隱藏、保留狀態，可再喚出
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SaveBounds();
            Hide(); // ESC 隱藏（不銷毀）
        }
        base.OnKeyDown(e);
    }

    /// <summary>套用記住的大小；位置若仍落在螢幕內則還原，否則置中。</summary>
    private void ApplyBounds()
    {
        if (_ui.WinWidth > 0) { Width = _ui.WinWidth; }
        if (_ui.WinHeight > 0) { Height = _ui.WinHeight; }
        if (_ui.WinLeft is double l && _ui.WinTop is double t && IsOnScreen(l, t, Width))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void SaveBounds()
    {
        var b = RestoreBounds;
        if (b.Width > 0 && !double.IsNaN(b.Left))
        {
            _ui.WinLeft = b.Left; _ui.WinTop = b.Top; _ui.WinWidth = b.Width; _ui.WinHeight = b.Height;
        }
        else
        {
            _ui.WinLeft = Left; _ui.WinTop = Top; _ui.WinWidth = ActualWidth; _ui.WinHeight = ActualHeight;
        }
        _ui.Save();
    }

    /// <summary>標題列可見且可點：頂邊在虛擬桌面內、左右各留至少 80px 在畫面上。</summary>
    private static bool IsOnScreen(double left, double top, double width)
    {
        double vsl = SystemParameters.VirtualScreenLeft;
        double vst = SystemParameters.VirtualScreenTop;
        double vsr = vsl + SystemParameters.VirtualScreenWidth;
        double vsb = vst + SystemParameters.VirtualScreenHeight;
        return top >= vst - 2 && top <= vsb - 40 && (left + width) >= vsl + 80 && left <= vsr - 80;
    }
}
