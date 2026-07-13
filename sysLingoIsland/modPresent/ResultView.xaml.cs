using System.Windows;
using System.Windows.Controls;
using LingoIsland.Query;
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
using Hyperlink = System.Windows.Documents.Hyperlink;
using Run = System.Windows.Documents.Run;
using UserControl = System.Windows.Controls.UserControl;

namespace LingoIsland.Present;

/// <summary>
/// 加入我的筆記之請求（Issue #55）：結果＋目標資料夾名（空＝依使用中情境/預設夾，由 App 解析）＋底色 hex。
/// 由結果檢視依當下「加入至／底色」選擇組成，供 App 加入指定夾並套色。
/// </summary>
public readonly record struct NoteAddRequest(QueryResult Result, string FolderName, string ColorHex);

/// <summary>
/// 查詢結果檢視（#135：自 <c>ResultWindow</c> 抽出為共用 <see cref="UserControl"/>，宿於 Dictionary 分頁）：
/// 三區直排原文／KK 音標／中譯（不加欄目標示、以字級/色彩/字體分層）；英文組（原文＋KK 音標）與中文組（中譯）
/// 各有獨立播放鈕與「自動播放」勾選。英文原文逐字可點＝查該單字（往前/往後導航返回原句），鉛筆鈕編輯原文重譯，
/// 底部「加入我的筆記」＋「自動加入筆記」＋底色色塊列。<b>不再是浮動視窗</b>——無位置記憶/失焦隱藏/ESC 關窗/單一守衛。
/// </summary>
public partial class ResultView : UserControl
{
    private ISpeechService? _speech;
    private QueryResult? _current; // 目前顯示中的結果（供「加入我的筆記」收藏）
    private string _activeThemeName = ""; // 使用中主題名（供「加入至」預設夾選項標籤與解析，#55）
    private string _currentColor = "";      // 目前選定底色 hex（空＝無底色，#55）
    private bool _wiring;                    // 建構下拉/色塊時抑制事件回寫

    /// <summary>按「加入我的筆記」或勾選「自動加入筆記」時觸發（傳加入請求：結果＋目標夾＋底色，#55）。</summary>
    public event Action<NoteAddRequest>? AddToNotesRequested;

    /// <summary>點擊結果中英文單字時觸發（複查回饋：查該單字，非發音）；App 跑 QueryWordAsync 後以 <see cref="PushWordResult"/> 回填。</summary>
    public event Action<string>? WordQueryRequested;

    /// <summary>編輯原文後按「重新翻譯」時觸發（複查回饋：辨識有誤時校正重查）；App 跑 QueryTextAsync 後以 <see cref="ReplaceCurrentResult"/> 回填。</summary>
    public event Action<string>? TextReQueryRequested;

    /// <summary>頂部輸入框按「Look up」或 Enter 時觸發（帶英文原文；App 依單字/整句決定查字義或翻譯）。v1.0.1：自 DictionaryPage 移入（兩列對調）。</summary>
    public event Action<string>? ManualQueryRequested;

    /// <summary>輸入下拉開啟時觸發：App 據此以查詢歷史填入下拉。v1.0.1：自 DictionaryPage 移入（兩列對調）。</summary>
    public event Action? HistoryRequested;

    // 導航堆疊（複查回饋）：主查詢＝reset 為單一，查單字＝push，往前/往後在堆疊內移動、不重查亦不自動加入筆記
    private readonly List<QueryResult> _history = new();
    private int _pos = -1;
    private bool _wordBusy; // 單字查詢進行中：游標等待＋忽略再點（避免連點，複查回饋）
    private System.Windows.Threading.DispatcherTimer? _wordClickTimer; // 單擊發音／雙擊查詢之判別計時（v1.0.1）
    private string _pendingWord = ""; // 待發音之單字（單擊排程、雙擊時取消改查詢）

    public ResultView()
    {
        InitializeComponent();
        AddNoteBtn.Click += (_, _) => RaiseAdd();
        BackBtn.Click += (_, _) => Navigate(-1);
        ForwardBtn.Click += (_, _) => Navigate(1);
        EditBtn.Click += (_, _) => ShowEditMode();
        UpdateNav();

        // 手動查詢輸入列（v1.0.1：自 DictionaryPage 移入，置於頂部工具列下方）：Look up/Enter 送查詢、開下拉取歷史＋鎖下拉寬度
        LookupBtn.Click += (_, _) => DoLookup();
        InputBox.KeyDown += OnInputKeyDown;
        InputBox.DropDownOpened += OnDropDownOpened;
        AutoAddChk.IsChecked = AutoAddSettings.Enabled;
        AutoAddChk.Checked += (_, _) => AutoAddSettings.Enabled = true;
        AutoAddChk.Unchecked += (_, _) => AutoAddSettings.Enabled = false;

        _currentColor = NoteDefaults.ColorHex; // 預設底色（#55）
        FolderCombo.SelectionChanged += OnFolderChanged;
        // 播音速度下拉（v1.0.1，USR 回饋）：選 50–200% → 寫入 SpeechRateSettings（SpeechService 每次朗讀讀取套用）
        SpeedCombo.SelectionChanged += (_, _) =>
        {
            if (SpeedCombo.SelectedItem is System.Windows.Controls.ComboBoxItem it
                && int.TryParse((it.Content?.ToString() ?? "").TrimEnd('%'), out var pct))
            {
                SpeechRateSettings.Percent = pct;
            }
        };
        BuildSwatches();
        BuildFolderCombo(); // 無情境資訊時先以預設建；App 設定 targets 後重建
    }

    /// <summary>是否已顯示過非空結果（供 App 判斷「喚回」時分頁是否已有內容，#135）。</summary>
    public bool HasResult => _current is { IsEmpty: false };

    /// <summary>設定變更後由 App 注入新語音服務（播放鈕閉包讀取欄位，避免用到已釋放的舊服務，#135）。</summary>
    public void UpdateSpeech(ISpeechService speech) => _speech = speech;

    /// <summary>
    /// 供 App 提供「加入至」下拉之來源（Issue #55）：頂層資料夾名清單＋使用中主題名（空＝無情境）。
    /// 於顯示結果前呼叫，重建下拉並依 <see cref="NoteDefaults.FolderName"/> 還原選擇。
    /// </summary>
    public void SetNoteTargets(IEnumerable<string> topFolderNames, string activeThemeName)
    {
        _activeThemeName = activeThemeName ?? "";
        _folderNames = topFolderNames?.ToList() ?? new List<string>();
        BuildFolderCombo();
    }

    private List<string> _folderNames = new();

    private void BuildFolderCombo()
    {
        _wiring = true;
        FolderCombo.Items.Clear();
        // 第一項＝依使用中情境/預設夾（映射 NoteDefaults.FolderName＝""）
        FolderCombo.Items.Add(_activeThemeName.Length > 0
            ? $"(Active theme: {_activeThemeName})"
            : "(Default: My Notes)");
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
        SwatchRow.Children.Add(MakeSwatch("None", ""));
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
            ToolTip = string.IsNullOrEmpty(hex) ? "No color" : name,
            Tag = hex,
        };
        if (string.IsNullOrEmpty(hex))
        {
            swatch.Child = new TextBlock
            {
                Text = "/",
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

    // ---- 手動查詢輸入列（v1.0.1：自 DictionaryPage 移入；USR 回饋「兩列對調」，置於頂部工具列下方） ----

    private void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            DoLookup();
            e.Handled = true;
        }
    }

    private void DoLookup()
    {
        var t = (InputBox.Text ?? "").Trim();
        if (t.Length == 0)
        {
            return;
        }
        ManualQueryRequested?.Invoke(t);
    }

    private void OnDropDownOpened(object? sender, EventArgs e)
    {
        HistoryRequested?.Invoke(); // 開下拉即向 App 取查詢歷史填入
        ConstrainDropDownWidth();   // 下拉清單寬度鎖回輸入框寬度
    }

    /// <summary>以查詢歷史（英文原文、新在前、去重）填入輸入下拉；保留使用者當前已鍵入文字。</summary>
    public void SetHistory(IEnumerable<string> originals)
    {
        var text = InputBox.Text;
        InputBox.ItemsSource = originals?.ToList();
        InputBox.Text = text; // 換 ItemsSource 不清掉使用者已輸入內容
    }

    private bool _dropDownWidthBound;

    /// <summary>
    /// 令歷史下拉與輸入框**同寬**（USR 回饋 v1.0.1）：原生可編輯 ComboBox 的 popup 僅綁 <c>MinWidth＝控制項寬</c>、
    /// 無寬度上限，歷史項目為整句英文原文時會把下拉撐得比輸入框寬。這裡把原生模板中的下拉外框
    /// <c>DropDownBorder</c>（取不到時退回 <c>PART_Popup</c> 內容）之 <c>MaxWidth</c> 綁定輸入框
    /// <see cref="System.Windows.FrameworkElement.ActualWidth"/>，並關掉水平捲軸（配合 ItemTemplate 之省略號），
    /// 使下拉恆等於輸入框寬度。於首次開啟綁定一次（模板內 popup 內容此時方實體化）。
    /// </summary>
    private void ConstrainDropDownWidth()
    {
        if (_dropDownWidthBound)
        {
            return;
        }
        var toInputWidth = new System.Windows.Data.Binding("ActualWidth") { Source = InputBox };
        System.Windows.FrameworkElement? popupContent = null;
        if (InputBox.Template?.FindName("DropDownBorder", InputBox) is System.Windows.FrameworkElement border)
        {
            popupContent = border;
        }
        else if (InputBox.Template?.FindName("PART_Popup", InputBox) is System.Windows.Controls.Primitives.Popup { Child: System.Windows.FrameworkElement child })
        {
            popupContent = child;
        }
        if (popupContent is null)
        {
            return; // 模板尚未實體化 popup 內容：不設旗標，下次開啟再試
        }
        popupContent.SetBinding(System.Windows.FrameworkElement.MaxWidthProperty, toInputWidth);
        // 關下拉內部水平捲軸：改讓過長項目以省略號截斷（配合 ItemTemplate 之 TextTrimming），不撐寬亦不出水平捲軸
        if (FindDescendant<System.Windows.Controls.ScrollViewer>(popupContent) is { } sv)
        {
            sv.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
        }
        _dropDownWidthBound = true;
    }

    /// <summary>深度優先找第一個指定型別的視覺子代（用於取原生 ComboBox 下拉 popup 內部之 ScrollViewer）。</summary>
    private static T? FindDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T hit)
            {
                return hit;
            }
            if (FindDescendant<T>(c) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    public void ShowLoading()
    {
        _current = null;
        BodyPanel.Children.Clear();
        BodyPanel.Children.Add(new TextBlock
        {
            Text = "Recognizing & translating…",
            Foreground = Brush("#8A5A6D"),
            FontSize = 22,
        });
    }

    /// <summary>主查詢結果（螢幕框選/雙擊/手動輸入）：重置導航堆疊、渲染、依設定自動播放與自動加入筆記。</summary>
    public void ShowResult(QueryResult r, ISpeechService speech)
    {
        _speech = speech;
        _history.Clear();
        _history.Add(r);
        _pos = 0;
        Render(r);
        UpdateNav();

        // 自動播放（勾選後框選完即播）：兩者皆勾則英文先、中文接續。僅主查詢自動播放，單字查詢/導航不自動播。
        if (!r.IsEmpty && AutoPlaySettings.English)
        {
            speech.Speak(r.Original, "en-US", stopPrevious: true);
        }
        if (!r.IsEmpty && AutoPlaySettings.Chinese)
        {
            speech.Speak(r.Translation, "zh-TW", stopPrevious: !AutoPlaySettings.English);
        }

        // 自動加入筆記（Issue #34）：**僅主查詢**（螢幕轉換為主）自動去重收藏；單字查詢（PushWordResult）不自動加入（複查回饋）
        if (!r.IsEmpty && AutoAddSettings.Enabled)
        {
            RaiseAdd();
        }
    }

    /// <summary>單字查詢結果推入導航堆疊並顯示（複查回饋）：不自動播放、**不自動加入筆記**（仍可手動加入）；結束等待游標。</summary>
    public void PushWordResult(QueryResult r)
    {
        EndWordLookup();
        if (_pos < _history.Count - 1)
        {
            _history.RemoveRange(_pos + 1, _history.Count - _pos - 1); // 截去前進歷史
        }
        _history.Add(r);
        _pos = _history.Count - 1;
        Render(r);
        UpdateNav();
    }

    /// <summary>往前(-1)/往後(+1)在導航堆疊內移動（快速返回原句）：重顯既有結果、不重查、不自動加入。</summary>
    private void Navigate(int delta)
    {
        int np = _pos + delta;
        if (np < 0 || np >= _history.Count)
        {
            return;
        }
        _pos = np;
        Render(_history[_pos]);
        UpdateNav();
    }

    private void UpdateNav()
    {
        BackBtn.IsEnabled = _pos > 0;
        ForwardBtn.IsEnabled = _pos >= 0 && _pos < _history.Count - 1;
    }

    /// <summary>單字查詢失敗時由 App 呼叫：結束等待游標與忙碌旗標（結果不變）。</summary>
    public void WordLookupFailed() => EndWordLookup();

    /// <summary>編輯重譯結果回填（複查回饋）：取代目前導航位置之結果並重繪；結束等待游標。不自動加入筆記。</summary>
    public void ReplaceCurrentResult(QueryResult r)
    {
        EndWordLookup();
        if (_pos >= 0 && _pos < _history.Count)
        {
            _history[_pos] = r;
        }
        else
        {
            _history.Clear();
            _history.Add(r);
            _pos = 0;
        }
        Render(r);
        UpdateNav();
    }

    /// <summary>進入編輯模式（複查回饋）：以 TextBox 呈現目前原文，供校正後「重新翻譯」。</summary>
    private void ShowEditMode()
    {
        if (_current is not { IsEmpty: false } cur)
        {
            return; // 無可編輯內容（載入中/空結果/錯誤）
        }
        BodyPanel.Children.Clear();
        var box = new System.Windows.Controls.TextBox
        {
            Text = cur.Original,
            FontSize = ResultDisplaySettings.FontSize, // #複查：編輯框比照基準字級
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            Padding = new Thickness(6),
        };
        BodyPanel.Children.Add(new TextBlock
        {
            Text = "Fix the English text, then re-translate:",
            Foreground = Brush("#8A5A6D"), FontSize = 13, Margin = new Thickness(0, 0, 0, 6),
        });
        BodyPanel.Children.Add(box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var goBtn = new Button
        {
            Content = "Re-translate", Padding = new Thickness(14, 6, 14, 6),
            Background = Brush("#F4C2D0"), Foreground = Brush("#6D3A4D"),
            BorderThickness = new Thickness(0), FontSize = 15, Cursor = Cursors.Hand,
        };
        goBtn.Click += (_, _) => CommitEdit(box.Text);
        var cancelBtn = new Button
        {
            Content = "Cancel", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
            Background = Brush("#66FFFFFF"), Foreground = Brush("#6D3A4D"),
            BorderBrush = Brush("#E4B7C6"), BorderThickness = new Thickness(1), FontSize = 15, Cursor = Cursors.Hand,
        };
        cancelBtn.Click += (_, _) => Render(cur); // 取消：還原顯示目前結果
        row.Children.Add(goBtn);
        row.Children.Add(cancelBtn);
        BodyPanel.Children.Add(row);
        box.Focus();
        box.SelectAll();
    }

    private void CommitEdit(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            return; // 空字串不重查
        }
        _wordBusy = true; // 沿用查詢忙碌態（等待游標＋擋單字連點）
        System.Windows.Input.Mouse.OverrideCursor = Cursors.Wait;
        TextReQueryRequested?.Invoke(t);
    }

    /// <summary>結束單字查詢忙碌態：清旗標、還原游標（僅在確為等待游標時清，免誤動他處覆寫）。</summary>
    private void EndWordLookup()
    {
        _wordBusy = false;
        if (System.Windows.Input.Mouse.OverrideCursor == Cursors.Wait)
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    /// <summary>渲染單一結果至內容區（不含導航/自動播放/自動加入之副作用；供主查詢、單字查詢、導航共用）。</summary>
    private void Render(QueryResult r)
    {
        _current = r;
        BodyPanel.Children.Clear();

        // 智能配色（#55）：AI 依規則建議底色 → 本則預選該色（仍可手動改）；無建議則沿用預設底色
        _currentColor = string.IsNullOrEmpty(r.SuggestedColor) ? NoteDefaults.ColorHex : r.SuggestedColor;
        HighlightSwatches();

        if (r.IsEmpty)
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = "No English text detected",
                Foreground = Brush("#9A6A82"),
                FontSize = 24,
            });
            return;
        }

        // 英文組：原文（逐字可點＝查該單字）＋ KK 音標 ＋ 整句播放/自動。
        // 三區不加欄目標示（Issue #40）：字級/色彩/字體本身分層、一望即知。
        BodyPanel.Children.Add(WordifiedOriginal(r.Original));
        BodyPanel.Children.Add(Value(r.Phonetic, "#9A6A82", ResultDisplaySettings.PhoneticSize, bold: false, font: "Georgia", topMargin: 6));
        BodyPanel.Children.Add(PlayRow("▶ Play",
            () => _speech?.Speak(r.Original, "en-US", stopPrevious: true),
            AutoPlaySettings.English, v => AutoPlaySettings.English = v));

        // 英文 / 中文 之間空白行
        BodyPanel.Children.Add(new Border { Height = 16 });

        // 中文組：中譯 ＋ 中文播放/自動
        BodyPanel.Children.Add(Value(r.Translation, "#3A2C33", ResultDisplaySettings.TranslationSize, bold: false));
        BodyPanel.Children.Add(PlayRow("▶ Play",
            () => _speech?.Speak(r.Translation, "zh-TW", stopPrevious: true),
            AutoPlaySettings.Chinese, v => AutoPlaySettings.Chinese = v));
    }

    public void ShowError(string message)
    {
        _current = null;
        BodyPanel.Children.Clear();
        BodyPanel.Children.Add(new TextBlock
        {
            Text = "Query failed",
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
            FontSize = 14, // 播音鈕比照一般內文字級（USR 回饋：不特別加大）
            Cursor = Cursors.Hand,
        };
        btn.Click += (_, _) => onPlay();
        row.Children.Add(btn);

        var chk = new CheckBox
        {
            Content = "Auto-play",
            IsChecked = autoInit,
            Foreground = Brush("#8A5A6D"),
            FontSize = 14, // 自動播放勾選比照一般內文字級（USR 回饋）
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        chk.Checked += (_, _) => onAutoChanged(true);
        chk.Unchecked += (_, _) => onAutoChanged(false);
        row.Children.Add(chk);

        return row;
    }

    /// <summary>
    /// 英文原文以逐字可點呈現（Issue #11 → 複查回饋改制）：每個單字為一個 <see cref="Hyperlink"/>，
    /// **點選即查詢該單字**（觸發 <see cref="WordQueryRequested"/>，App 跑文字查詢後 <see cref="PushWordResult"/> 回填、
    /// 可經往前鈕返回原句）；標點與空白為不可點的純文字。切分規則見 <see cref="EnglishWordTokenizer"/>。
    /// </summary>
    private TextBlock WordifiedOriginal(string text)
    {
        var tb = new TextBlock
        {
            FontSize = ResultDisplaySettings.FontSize, // #複查：查詢視窗基準字級（選項頁可調）
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
                ToolTip = $"Click to pronounce · double-click to look up “{word}”",
            };
            link.Click += (_, _) => OnWordClick(word); // 單擊＝發音、雙擊＝進一步查詢（v1.0.1）
            tb.Inlines.Add(link);
        }
        return tb;
    }

    /// <summary>
    /// 點單字（v1.0.1，USR 回饋）：**單擊＝發音**（en-US 朗讀）、**雙擊＝進一步查詢該字字義**。
    /// 以計時器辨別——首擊排程發音、於雙擊視窗內對同字再點則取消發音改查詢（避免雙擊時先誤發音）。
    /// </summary>
    private void OnWordClick(string word)
    {
        if (_wordBusy) // 查詢中：忽略連點
        {
            return;
        }
        if (_wordClickTimer is { IsEnabled: true } && string.Equals(_pendingWord, word, StringComparison.Ordinal))
        {
            _wordClickTimer.Stop(); // 雙擊同字 → 取消發音、改查詢
            _pendingWord = "";
            _wordBusy = true;
            System.Windows.Input.Mouse.OverrideCursor = Cursors.Wait; // 等待游標，直到 PushWordResult/失敗清除
            WordQueryRequested?.Invoke(word);
            return;
        }
        _pendingWord = word; // 首擊 → 排程發音（雙擊視窗結束仍單擊才發）
        _wordClickTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _wordClickTimer.Stop();
        _wordClickTimer.Tick -= OnWordClickElapsed;
        _wordClickTimer.Tick += OnWordClickElapsed;
        _wordClickTimer.Start();
    }

    private void OnWordClickElapsed(object? sender, EventArgs e)
    {
        _wordClickTimer?.Stop();
        var w = _pendingWord;
        _pendingWord = "";
        if (!string.IsNullOrEmpty(w))
        {
            _speech?.Speak(w, "en-US", stopPrevious: true); // 單擊確認 → 朗讀該字
        }
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
}
