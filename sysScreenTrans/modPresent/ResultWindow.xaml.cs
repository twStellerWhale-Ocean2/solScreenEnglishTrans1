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
using Hyperlink = System.Windows.Documents.Hyperlink;
using Run = System.Windows.Documents.Run;

namespace ScreenTrans.Present;

/// <summary>
/// 浮動結果視窗（[runWi自訂Usr查看聆聽結果]、design ＜III.C.(C)＞ 查詢結果頁）：
/// 淺粉底、大字；英文組（原文＋KK音標）與中文組（中譯）各有獨立播放鈕與「自動播放」勾選，
/// 兩組間留空白行。勾選自動播放者，結果一出即朗讀對應語言。
/// 關閉改由明確操作觸發：ESC／關閉鈕／下一次查詢取代（失焦不自動關閉，切換視窗對照時結果保留）。
/// 視窗可拖曳標題移動、右下握把縮放；關閉時記住位置與大小（UiStateStore），下次開啟還原。
/// </summary>
public partial class ResultWindow : Window
{
    private ISpeechService? _speech;
    private bool _closing;
    private readonly UiStateStore _ui;
    private QueryResult? _current; // 目前顯示中的結果（供「加入我的筆記」收藏）

    /// <summary>是否已進入關閉序列（供呼叫端避免對關閉中視窗重複 Close，Issue #32）。</summary>
    public bool IsClosing => _closing;

    /// <summary>按「展示歷史紀錄」時觸發（呼叫端開查詢歷史視窗，spec#6）。</summary>
    public event Action? HistoryRequested;

    /// <summary>按「展示我的筆記」時觸發（呼叫端開我的筆記視窗，spec#7）。</summary>
    public event Action? NotesRequested;

    /// <summary>按「加入我的筆記」時觸發（傳目前結果，呼叫端去重加入並 toast，spec#7）。</summary>
    public event Action<QueryResult>? AddToNotesRequested;

    public ResultWindow()
    {
        InitializeComponent();
        _ui = UiStateStore.Load();
        ApplyBounds();
        HeaderBar.MouseLeftButtonDown += OnHeaderDrag;
        ResizeGrip.DragDelta += OnResizeDelta;
        CloseBtn.Click += (_, _) => CloseOnce();
        HistoryBtn.Click += (_, _) => HistoryRequested?.Invoke();
        NotesBtn.Click += (_, _) => NotesRequested?.Invoke();
        AddNoteBtn.Click += (_, _) =>
        {
            if (_current is { IsEmpty: false } r)
            {
                AddToNotesRequested?.Invoke(r);
            }
        };
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

    /// <summary>只關一次：避免關閉過程中 ESC／關閉鈕／取代流程重複呼叫 Close（WPF 會擲「closing」例外）。</summary>
    private void CloseOnce()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
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
        _current = null;
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
        _speech = speech;
        _current = r;
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

        // 英文組：原文（逐字可點單獨發音）＋ KK 音標 ＋ 整句播放/自動
        BodyPanel.Children.Add(Label("原文 ORIGINAL"));
        BodyPanel.Children.Add(WordifiedOriginal(r.Original));
        BodyPanel.Children.Add(Label("KK 音標 PHONETIC"));
        BodyPanel.Children.Add(Value(r.Phonetic, "#9A6A82", 24, bold: false, font: "Georgia"));
        BodyPanel.Children.Add(PlayRow("▶ 整句發音",
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
        _current = null;
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

    /// <summary>
    /// 英文原文以逐字可點呈現（Issue #11）：每個單字為一個 <see cref="Hyperlink"/>，
    /// 點選即以 en-US 單獨朗讀該字（重複觸發先停再播）；標點與空白為不可點的純文字，
    /// 整句朗讀與自動播放不受影響。切分規則見 <see cref="EnglishWordTokenizer"/>。
    /// </summary>
    private TextBlock WordifiedOriginal(string text)
    {
        var tb = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#3A2C33"),
            TextWrapping = TextWrapping.Wrap,
        };
        foreach (var tok in EnglishWordTokenizer.Tokenize(text))
        {
            if (!tok.IsWord)
            {
                tb.Inlines.Add(new Run(tok.Text)); // 空白/標點：原樣、不可點
                continue;
            }
            string word = tok.Text;
            var link = new Hyperlink(new Run(word))
            {
                Foreground = Brush("#3A2C33"), // 維持大字原色，不用預設藍色連結色
                TextDecorations = WordUnderline, // 淡粉點狀底線＝可點提示（游標另呈手形）
                Cursor = Cursors.Hand,
                ToolTip = $"朗讀「{word}」",
            };
            link.Click += (_, _) => _speech?.Speak(word, "en-US", stopPrevious: true);
            tb.Inlines.Add(link);
        }
        return tb;
    }

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

    /// <summary>逐字單字的淡粉點狀底線（可點提示）；凍結以供所有單字共用、不重複建立。</summary>
    private static readonly System.Windows.TextDecorationCollection WordUnderline = BuildWordUnderline();

    private static System.Windows.TextDecorationCollection BuildWordUnderline()
    {
        var pen = new System.Windows.Media.Pen(Brush("#C77D9A"), 1.6)
        {
            DashStyle = System.Windows.Media.DashStyles.Dash,
        };
        var deco = new System.Windows.TextDecoration
        {
            Location = System.Windows.TextDecorationLocation.Underline,
            Pen = pen,
            PenThicknessUnit = System.Windows.TextDecorationUnit.Pixel,
        };
        var col = new System.Windows.TextDecorationCollection { deco };
        col.Freeze();
        return col;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseOnce();
        }
    }
}
