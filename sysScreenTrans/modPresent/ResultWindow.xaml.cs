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
using CancelEventArgs = System.ComponentModel.CancelEventArgs;
using Hyperlink = System.Windows.Documents.Hyperlink;
using Run = System.Windows.Documents.Run;

namespace ScreenTrans.Present;

/// <summary>
/// 加入我的筆記之請求（Issue #55）：結果＋目標資料夾名（空＝依使用中情境/預設夾，由 App 解析）＋底色 hex。
/// 由結果視窗依當下「加入至／底色」選擇組成，供 App 加入指定夾並套色。
/// </summary>
public readonly record struct NoteAddRequest(QueryResult Result, string FolderName, string ColorHex);

/// <summary>
/// 浮動結果視窗（[runWi自訂Usr查看聆聽結果]、design ＜III.C.(C)＞ 查詢結果頁）：
/// 淺粉底、大字；英文組（原文＋KK音標）與中文組（中譯）各有獨立播放鈕與「自動播放」勾選，
/// 兩組間留空白行。勾選自動播放者，結果一出即朗讀對應語言。
/// <b>標準表單（Issue #59）</b>：標準標題列（OS 字型/標題）＋標準邊框拖拉縮放（<c>ResizeMode=CanResize</c>）
/// ＋工作列按鈕（<c>ShowInTaskbar</c>，失焦後仍可尋、不再像被隱藏）；Topmost 浮於遊戲上「一直存在」，
/// 同時至多一個（呼叫端 <c>App.CloseResult</c> 取代前一個）。
/// 關閉由明確操作觸發：ESC／標題列關閉鈕／下一次查詢取代（失焦不自動關閉，切換視窗對照時結果保留）。
/// 關閉時記住位置與大小（UiStateStore），下次開啟還原。
/// </summary>
public partial class ResultWindow : Window
{
    private ISpeechService? _speech;
    private bool _closing;
    private readonly UiStateStore _ui;
    private QueryResult? _current; // 目前顯示中的結果（供「加入我的筆記」收藏）
    private string _activeContextName = ""; // 使用中情境名（供「加入至」預設夾選項標籤與解析，#55）
    private string _currentColor = "";      // 目前選定底色 hex（空＝無底色，#55）
    private bool _wiring;                    // 建構下拉/色塊時抑制事件回寫

    /// <summary>是否已進入關閉序列（供呼叫端避免對關閉中視窗重複 Close，Issue #32）。</summary>
    public bool IsClosing => _closing;

    /// <summary>按「加入我的筆記」或勾選「自動加入筆記」時觸發（傳加入請求：結果＋目標夾＋底色，#55）。</summary>
    public event Action<NoteAddRequest>? AddToNotesRequested;

    public ResultWindow()
    {
        InitializeComponent();
        _ui = UiStateStore.Load();
        ApplyBounds();
        // 移動/縮放/關閉皆由 OS 標準 chrome 提供（Issue #59），不再自訂 HeaderBar 拖曳/Thumb 握把/關閉鈕。
        AddNoteBtn.Click += (_, _) => RaiseAdd();
        AutoAddChk.IsChecked = AutoAddSettings.Enabled;
        AutoAddChk.Checked += (_, _) => AutoAddSettings.Enabled = true;
        AutoAddChk.Unchecked += (_, _) => AutoAddSettings.Enabled = false;

        _currentColor = NoteDefaults.ColorHex; // 預設底色（#55）
        FolderCombo.SelectionChanged += OnFolderChanged;
        BuildSwatches();
        BuildFolderCombo(); // 無情境資訊時先以預設建；App 設定 targets 後重建
    }

    /// <summary>
    /// 供 App 提供「加入至」下拉之來源（Issue #55）：頂層資料夾名清單＋使用中情境名（空＝無情境）。
    /// 於顯示結果前呼叫，重建下拉並依 <see cref="NoteDefaults.FolderName"/> 還原選擇。
    /// </summary>
    public void SetNoteTargets(IEnumerable<string> topFolderNames, string activeContextName)
    {
        _activeContextName = activeContextName ?? "";
        _folderNames = topFolderNames?.ToList() ?? new List<string>();
        BuildFolderCombo();
    }

    private List<string> _folderNames = new();

    private void BuildFolderCombo()
    {
        _wiring = true;
        FolderCombo.Items.Clear();
        // 第一項＝依使用中情境/預設夾（映射 NoteDefaults.FolderName＝""）
        FolderCombo.Items.Add(_activeContextName.Length > 0
            ? $"（使用中情境：{_activeContextName}）"
            : "（預設：我的筆記）");
        foreach (var n in _folderNames)
        {
            FolderCombo.Items.Add(n);
        }
        // 還原選擇：NoteDefaults.FolderName 非空且存在於清單 → 選之；否則選第一項（情境/預設）
        int idx = 0;
        if (NoteDefaults.FolderName.Length > 0)
        {
            int found = _folderNames.FindIndex(n => string.Equals(n, NoteDefaults.FolderName, StringComparison.Ordinal));
            idx = found >= 0 ? found + 1 : 0;
        }
        FolderCombo.SelectedIndex = idx;
        _wiring = false;
    }

    private void OnFolderChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_wiring)
        {
            return;
        }
        // 第一項＝情境/預設（FolderName＝""）；其餘＝固定夾名。改選即記為預設（#55）
        NoteDefaults.FolderName = FolderCombo.SelectedIndex <= 0 ? "" : (FolderCombo.SelectedItem as string ?? "");
        NoteDefaults.Save();
    }

    /// <summary>目前「加入至」選擇之資料夾名（空＝依使用中情境/預設夾，由 App 解析）。</summary>
    private string CurrentFolderName() => FolderCombo.SelectedIndex <= 0 ? "" : (FolderCombo.SelectedItem as string ?? "");

    // ---- 底色色塊列（#55：無＋粉彩盤；點選即設當前底色並記為預設；智能配色下自動預選 AI 建議色） ----

    private void BuildSwatches()
    {
        SwatchRow.Children.Clear();
        SwatchRow.Children.Add(MakeSwatch("無", ""));
        foreach (var (name, hex) in NoteColors.Palette)
        {
            SwatchRow.Children.Add(MakeSwatch(name, hex));
        }
        HighlightSwatches();
    }

    private System.Windows.Controls.Border MakeSwatch(string name, string hex)
    {
        var swatch = new System.Windows.Controls.Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new System.Windows.CornerRadius(4),
            Background = string.IsNullOrEmpty(hex) ? Brush("#FFFFFF") : Brush(hex),
            BorderBrush = Brush("#D8B4C2"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 5, 0),
            Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrEmpty(hex) ? "無底色" : name,
            Tag = hex,
        };
        if (string.IsNullOrEmpty(hex))
        {
            swatch.Child = new TextBlock
            {
                Text = "／",
                Foreground = Brush("#B0688A"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        swatch.MouseLeftButtonDown += (_, _) =>
        {
            _currentColor = hex;
            NoteDefaults.ColorHex = hex; // 改選即記為預設（#55）
            NoteDefaults.Save();
            HighlightSwatches();
        };
        return swatch;
    }

    /// <summary>依 <see cref="_currentColor"/> 標示選定色塊（加粗深框）。</summary>
    private void HighlightSwatches()
    {
        foreach (var child in SwatchRow.Children)
        {
            if (child is System.Windows.Controls.Border b)
            {
                bool sel = string.Equals((b.Tag as string) ?? "", _currentColor, StringComparison.OrdinalIgnoreCase);
                b.BorderBrush = sel ? Brush("#2F6FED") : Brush("#D8B4C2");
                b.BorderThickness = new Thickness(sel ? 2.5 : 1);
            }
        }
    }

    /// <summary>組成加入請求並觸發（供「加入我的筆記」與自動加入共用，#55）。</summary>
    private void RaiseAdd()
    {
        if (_current is { IsEmpty: false } r)
        {
            AddToNotesRequested?.Invoke(new NoteAddRequest(r, CurrentFolderName(), _currentColor));
        }
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

        // 智能配色（#55）：AI 依規則建議底色 → 本則預選該色（仍可手動改）；無建議則沿用預設底色
        _currentColor = string.IsNullOrEmpty(r.SuggestedColor) ? NoteDefaults.ColorHex : r.SuggestedColor;
        HighlightSwatches();

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

        // 英文組：原文（逐字可點單獨發音）＋ KK 音標 ＋ 整句播放/自動。
        // 三區不加欄目標示（Issue #40）：字級/色彩/字體本身分層、一望即知。
        BodyPanel.Children.Add(WordifiedOriginal(r.Original));
        BodyPanel.Children.Add(Value(r.Phonetic, "#9A6A82", 24, bold: false, font: "Georgia", topMargin: 6));
        BodyPanel.Children.Add(PlayRow("▶ 整句發音",
            () => _speech?.Speak(r.Original, "en-US", stopPrevious: true),
            AutoPlaySettings.English, v => AutoPlaySettings.English = v));

        // 英文 / 中文 之間空白行
        BodyPanel.Children.Add(new Border { Height = 16 });

        // 中文組：中譯 ＋ 中文播放/自動
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

        // 自動加入筆記（Issue #34）：勾選後查詢成功即去重收藏，套當前資料夾/底色選擇（#55）
        if (AutoAddSettings.Enabled)
        {
            RaiseAdd();
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

    private static TextBlock Value(string t, string color, double size, bool bold, string? font = null, double topMargin = 0)
    {
        var tb = new TextBlock
        {
            Text = t,
            Foreground = Brush(color),
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, topMargin, 0, 0),
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
