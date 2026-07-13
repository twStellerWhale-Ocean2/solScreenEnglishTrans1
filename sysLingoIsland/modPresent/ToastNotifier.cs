using System.Windows.Media.Animation;
using Window = System.Windows.Window;
using WindowStyle = System.Windows.WindowStyle;
using ResizeMode = System.Windows.ResizeMode;
using SizeToContent = System.Windows.SizeToContent;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using Thickness = System.Windows.Thickness;
using CornerRadius = System.Windows.CornerRadius;
using UIElement = System.Windows.UIElement;
using SystemParameters = System.Windows.SystemParameters;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace LingoIsland.Present;

/// <summary>
/// 右下角淡入淡出 toast（[modPresent模組] 我的筆記檢視契約，spec#7）：不搶焦、不擋遊戲、逾時自動消失。
/// 供「加入我的筆記」回饋「已加入」／「已在筆記中」。以無邊框 topmost 非啟用小視窗呈現、命中穿透。
/// </summary>
public static class ToastNotifier
{
    public static void Show(string message)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            Child = new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 14, TextWrapping = System.Windows.TextWrapping.Wrap, MaxWidth = 360 },
        };

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,          // 不搶焦（不打斷遊戲/前景視窗）
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,       // 命中穿透、不擋點擊
            Opacity = 0,
            Content = border,
        };

        win.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea; // 排除工作列
            win.Left = area.Right - win.ActualWidth - 24;
            win.Top = area.Bottom - win.ActualHeight - 24;
            win.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        };
        win.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
            fadeOut.Completed += (_, _) => win.Close();
            win.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        timer.Start();
    }
}
