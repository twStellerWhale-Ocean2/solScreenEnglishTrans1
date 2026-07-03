using System.Windows;
using System.Windows.Controls;
using ScreenTrans.Query;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DragDeltaEventArgs = System.Windows.Controls.Primitives.DragDeltaEventArgs;
using CancelEventArgs = System.ComponentModel.CancelEventArgs;

namespace ScreenTrans.Present;

/// <summary>
/// 浮動結果視窗（[runWi自訂Usr查看聆聽結果]、design ＜III.C.(C)＞ 查詢結果頁）：
/// 淺粉底、大字；英文組（原文＋KK音標）與中文組（中譯）各有獨立播放鈕與「自動播放」勾選，
/// 兩組間留空白行。勾選自動播放者，結果一出即朗讀對應語言。ESC 或點視窗外即關。
/// 視窗可拖曳標題移動、右下握把縮放；關閉時記住位置與大小（UiStateStore），下次開啟還原。
/// </summary>
public partial class ResultWindow : Window
{
    private ISpeechService? _speech;
    private bool _isLoading;
    private readonly UiStateStore _ui;

    public ResultWindow()
    {
        InitializeComponent();
        _ui = UiStateStore.Load();
        ApplyBounds();
        HeaderBar.MouseLeftButtonDown += OnHeaderDrag;
        ResizeGrip.DragDelta += OnResizeDelta;
    }

    /// <summary>套用記住的大小；位置若仍落在螢幕內則還原，否則置中。</summary>
    private void ApplyBounds()
    {
        Width = _ui.WinWidth;
        Height = _ui.WinHeight;
        if (_ui.WinLeft is double l && _ui.WinTop is double t && IsOnScreen(l, t, _ui.WinWidth))
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

    /// <summary>標題列可見且可點：頂邊在虛擬桌面內、左右各留至少 80px 在畫面上。</summary>
    private static bool IsOnScreen(double left, double top, double width)
    {
        double vsl = SystemParameters.VirtualScreenLeft;
        double vst = SystemParameters.VirtualScreenTop;
        double vsr = vsl + SystemParameters.VirtualScreenWidth;
        double vsb = vst + SystemParameters.VirtualScreenHeight;
        return top >= vst - 2 && top <= vsb - 40 && (left + width) >= vsl + 80 && left <= vsr - 80;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch { /* 非左鍵按住狀態的邊界情形，忽略 */ }
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var b = RestoreBounds;
        if (b.Width > 0 && !double.IsNaN(b.Left))
        {
            _ui.WinLeft = b.Left;
            _ui.WinTop = b.Top;
            _ui.WinWidth = b.Width;
            _ui.WinHeight = b.Height;
        }
        else
        {
            _ui.WinLeft = Left;
            _ui.WinTop = Top;
            _ui.WinWidth = ActualWidth;
            _ui.WinHeight = ActualHeight;
        }
        _ui.Save();
        base.OnClosing(e);
    }

    public void ShowLoading()
    {
        _isLoading = true;
        BodyPanel.Children.Clear();
        BodyPanel.Children.Add(new TextBlock
        {
            Text = "辨識翻譯中…",
            Foreground = Brush("#8A5A6D"),
            FontSize = 22,
        });
    }

    public void ShowResult(QueryResult r, ISpeechService speech)
    {
        _isLoading = false;
        _speech = speech;
        BodyPanel.Children.Clear();

        if (r.IsEmpty)
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = "未偵測到英文文字",
                Foreground = Brush("#9A6A82"),
                FontSize = 24,
            });
            return;
        }

        // 英文組：原文 ＋ KK 音標 ＋ 英文播放/自動
        BodyPanel.Children.Add(Label("原文 ORIGINAL"));
        BodyPanel.Children.Add(Value(r.Original, "#3A2C33", 28, bold: true));
        BodyPanel.Children.Add(Label("KK 音標 PHONETIC"));
        BodyPanel.Children.Add(Value(r.Phonetic, "#9A6A82", 24, bold: false, font: "Georgia"));
        BodyPanel.Children.Add(PlayRow("▶ 英文發音",
            () => _speech?.Speak(r.Original, "en-US", stopPrevious: true),
            AutoPlaySettings.English, v => AutoPlaySettings.English = v));

        // 英文 / 中文 之間空白行
        BodyPanel.Children.Add(new Border { Height = 16 });

        // 中文組：中譯 ＋ 中文播放/自動
        BodyPanel.Children.Add(Label("中譯 TRANSLATION"));
        BodyPanel.Children.Add(Value(r.Translation, "#3A2C33", 26, bold: false));
        BodyPanel.Children.Add(PlayRow("▶ 中文發音",
            () => _speech?.Speak(r.Translation, "zh-TW", stopPrevious: true),
            AutoPlaySettings.Chinese, v => AutoPlaySettings.Chinese = v));

        // 自動播放（勾選後框選完即播）：兩者皆勾則英文先、中文接續
        if (AutoPlaySettings.English)
        {
            speech.Speak(r.Original, "en-US", stopPrevious: true);
        }
        if (AutoPlaySettings.Chinese)
        {
            speech.Speak(r.Translation, "zh-TW", stopPrevious: !AutoPlaySettings.English);
        }
    }

    public void ShowError(string message)
    {
        _isLoading = false;
        BodyPanel.Children.Clear();
        BodyPanel.Children.Add(new TextBlock
        {
            Text = "查詢失敗",
            Foreground = Brush("#C0506D"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        });
        BodyPanel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brush("#5A3A47"),
            FontSize = 20,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
    }

    private StackPanel PlayRow(string label, Action onPlay, bool autoInit, Action<bool> onAutoChanged)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var btn = new Button
        {
            Content = label,
            Padding = new Thickness(14, 6, 14, 6),
            Background = Brush("#F4C2D0"),
            Foreground = Brush("#6D3A4D"),
            BorderThickness = new Thickness(0),
            FontSize = 18,
            Cursor = Cursors.Hand,
        };
        btn.Click += (_, _) => onPlay();
        row.Children.Add(btn);

        var chk = new CheckBox
        {
            Content = "自動播放",
            IsChecked = autoInit,
            Foreground = Brush("#8A5A6D"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        chk.Checked += (_, _) => onAutoChanged(true);
        chk.Unchecked += (_, _) => onAutoChanged(false);
        row.Children.Add(chk);

        return row;
    }

    private static TextBlock Label(string t) => new()
    {
        Text = t,
        Foreground = Brush("#B0688A"),
        FontSize = 16,
        Margin = new Thickness(0, 8, 0, 2),
    };

    private static TextBlock Value(string t, string color, double size, bool bold, string? font = null)
    {
        var tb = new TextBlock
        {
            Text = t,
            Foreground = Brush(color),
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        };
        if (bold)
        {
            tb.FontWeight = FontWeights.SemiBold;
        }
        if (font is not null)
        {
            tb.FontFamily = new FontFamily(font);
        }
        return tb;
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_isLoading)
        {
            Close();
        }
    }
}
