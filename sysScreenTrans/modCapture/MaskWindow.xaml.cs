using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ScreenTrans.Capture;

/// <summary>
/// 全螢幕變暗遮罩＋橡皮筋選區（[runWi自訂Usr熱鍵喚起框選]、design ＜III.C.(C)＞ 選區遮罩頁）。
/// 覆蓋整個虛擬桌面（多螢幕）；放開滑鼠即以 physical pixels 截圖，ESC 任一時點取消。
/// </summary>
public partial class MaskWindow : Window
{
    private Point _start;
    private bool _dragging;

    /// <summary>選取完成之截圖結果；取消或空選為 null。</summary>
    public CaptureResult? Result { get; private set; }

    public MaskWindow()
    {
        InitializeComponent();
        // 覆蓋整個虛擬桌面（DIU；跨多螢幕）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        // 提示置於主螢幕頂部中央附近
        Loaded += (_, _) =>
        {
            Canvas.SetLeft(HintBorder, (Width - HintBorder.ActualWidth) / 2);
            Canvas.SetTop(HintBorder, 24);
            Focus();
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // 雙擊＝自動判斷模式（Issue #54）：截整螢幕、標記游標處，交查詢層判斷該處那句
        if (e.ClickCount == 2)
        {
            DoPointCapture(e.GetPosition(RootCanvas));
            return;
        }
        _start = e.GetPosition(RootCanvas);
        _dragging = true;
        Canvas.SetLeft(SelRect, _start.X);
        Canvas.SetTop(SelRect, _start.Y);
        SelRect.Width = 0;
        SelRect.Height = 0;
        SelRect.Visibility = Visibility.Visible;
        HintBorder.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var p = e.GetPosition(RootCanvas);
        Canvas.SetLeft(SelRect, Math.Min(p.X, _start.X));
        Canvas.SetTop(SelRect, Math.Min(p.Y, _start.Y));
        SelRect.Width = Math.Abs(p.X - _start.X);
        SelRect.Height = Math.Abs(p.Y - _start.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        SelRect.Visibility = Visibility.Collapsed;
        var end = e.GetPosition(RootCanvas);

        // 選區左上、右下兩角 → physical pixels（PerMonitorV2 下 PointToScreen 回裝置像素）
        var topLeft = RootCanvas.PointToScreen(new Point(Math.Min(_start.X, end.X), Math.Min(_start.Y, end.Y)));
        var bottomRight = RootCanvas.PointToScreen(new Point(Math.Max(_start.X, end.X), Math.Max(_start.Y, end.Y)));
        int px = (int)Math.Round(topLeft.X);
        int py = (int)Math.Round(topLeft.Y);
        int pw = (int)Math.Round(bottomRight.X - topLeft.X);
        int ph = (int)Math.Round(bottomRight.Y - topLeft.Y);

        // 選區過小或異常尺寸 → 非有效框選（可能是雙擊之單擊或誤點）：復位、遮罩留著（唯 ESC 取消，Issue #54）；
        // 防超大 Bitmap 造成 OOM 亦以復位處理。
        if (pw < 3 || ph < 3 || pw > 30000 || ph > 30000)
        {
            HintBorder.Visibility = Visibility.Visible;
            return;
        }

        // 遮罩先隱藏、等一個 render cycle 讓它從畫面移除，再截圖（避免截到遮罩自身）；
        // 用 Close() 結束 modal，不設 DialogResult（Hide 後設 DialogResult 會拋例外）。
        Hide();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        Result = ScreenCapture.Capture(px, py, pw, ph);
        Close();
    }

    /// <summary>
    /// 雙擊自動判斷模式（Issue #54）：截取整個虛擬桌面（physical px）並於游標處畫紅色標記，
    /// 交查詢層依標記辨識該處那句英文。遮罩先隱藏再截圖（避免截到遮罩自身）。
    /// </summary>
    private void DoPointCapture(Point p)
    {
        _dragging = false;
        ReleaseMouseCapture();
        SelRect.Visibility = Visibility.Collapsed;

        var tl = RootCanvas.PointToScreen(new Point(0, 0));
        var br = RootCanvas.PointToScreen(new Point(Width, Height));
        int px = (int)Math.Round(tl.X);
        int py = (int)Math.Round(tl.Y);
        int pw = (int)Math.Round(br.X - tl.X);
        int ph = (int)Math.Round(br.Y - tl.Y);
        var cur = RootCanvas.PointToScreen(p);
        int mx = (int)Math.Round(cur.X) - px;
        int my = (int)Math.Round(cur.Y) - py;

        Hide();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        Result = ScreenCapture.CaptureWithMarker(px, py, pw, ph, mx, my);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            Close();
        }
    }
}
