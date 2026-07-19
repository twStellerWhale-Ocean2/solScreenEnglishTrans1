using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using System.Text.Json;
using LingoIsland.Video;
using LingoIsland.Query;

namespace LingoIsland.Present;

/// <summary>
/// 影片擷取分頁（[modVideoCapture模組]／[techApp桌面查詢工具] 擷取來源頁，spec#2）：【獲得】單一輸入框貼影片網址＋字幕檔網址（epic #178 增量6′）→
/// 取字幕檔（<see cref="TranscriptFetch"/>）以 <see cref="SubtitleParser"/> 直接解析出**自帶之時間＋說話人**（增量6′-B「時間 pivot」定案：不對齊、不 Whisper——那些會把時間弄亂序）→摘要確認→載入→
/// WebView2 內嵌 YouTube IFrame Player API 導引播放、<see cref="PauseDecider"/> 到句暫停顯字幕→暫停句逐字可點（<see cref="WordLookupRequested"/>，沿用既有查詢）→
/// 加入既有筆記（<see cref="AddToNotesRequested"/>）。時間偏移量供整體微調。與螢幕擷取並列之可插拔擷取來源、下游完全共用。
/// </summary>
public partial class VideoCapturePage : System.Windows.Controls.UserControl
{
    private readonly VideoStore _videoStore;                       // 影片清單持久化（epic #145 增量4）
    private readonly ThemeStore _themes;                           // 使用中主題（加入影片時記錄跨媒體歸屬）＋依 theme 篩選（B）＋內容區塊所屬主題指派（#173）
    private bool _populatingVideoFilter;                           // 重填篩選下拉期間抑制 SelectionChanged→重整
    private bool _populatingVideoPicker;                           // 重填「所屬主題」下拉期間抑制 SelectionChanged→重指派（#173）
    private readonly SubtitleStore _subs;                          // 字幕存檔：免重抓、保留說話人/YAML 編修（#174）
    private readonly ITranscriptAligner _aligner;                  // 直接抽取（epic #178 增量6′-B「時間 pivot」）：免費解析讀不到時間之網頁,以 AI 逐句抽「時間＋說話人＋台詞」（時間照網頁原樣抄、非估算/對齊,故不亂序）；會用 API、跑前確認費用
    private readonly VideoSubtitleStatusStore _statusStore = new();  // 字幕狀態快取（#188→增量6′）：獲得框存入字幕檔網址＋載入來源，載入未快取時取用建立字幕
    private string? _currentVideoItemId;                           // 目前載入影片於清單之項 Id（供更新標題／選中）
    private string? _currentVideoId;                               // 目前載入影片之 YouTube ID（字幕存檔鍵，#174）
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _cueClickTimer;               // 區分字幕清單單擊（選取/播放暫停切換）與雙擊（跳轉）
    private bool _cueClickWasSelected;                             // 按下當下該句是否**已選中**（單擊已選中者才播/暫停切換）
    private bool _cueDoubleClicking;                               // 雙擊進行中：抑制其第二次放開再武裝單擊計時（否則雙擊跳轉暫停後又被單擊 toggle 續播）
    private IReadOnlyList<SubtitleCue> _cues = new List<SubtitleCue>();
    private int _lastPausedIndex = -1; // 上次已暫停之 cue（PauseDecider 用）
    private int _shownCue = -1;        // 目前字幕帶顯示之 cue
    private bool _webReady;
    private Task? _webInit;            // WebView2 單次初始化任務（避 Loaded 與 Load 併發重複 CreateAsync 擲例外）
    private bool _guiding;             // 導引播放中（輪詢到句暫停生效）
    private bool _polling;             // OnPoll 重入防護（async void 掛 100ms timer、橋接往返可能 >100ms）
    private bool _playbackStarted;     // 實際起播確認後才宣稱「逐句暫停」成功（避可嵌入被禁時謊報）
    private bool _loading;             // 抓字幕中（防重入抓字幕）
    private CancellationTokenSource? _loadCts; // 抓字幕可取消（新 Load／取消鈕）

    // 說話人字幕（epic #145 增量5）：CueList 綁 CueRow view-model（保留原始 _cues index，篩選/顯示不動播放 index）
    private List<CueRow> _rows = new();
    private System.ComponentModel.ICollectionView? _cueView; // _rows 之預設檢視，套說話人篩選
    private bool _yamlEditing;         // 整檔 YAML 編修模式中
    private string? _currentTitle;     // 目前影片標題（起播後取得，供 AI 推斷輔助判斷角色）（增量6）
    private bool _populatingModes;     // 填模式下拉/勾選面板期間抑制 SelectionChanged／勾選事件

    // 說話人顯示模式與等待模式（#189-checklist USR）：篩選/顯示三態＋等待兩態；對象取自共用之勾選面板 _speakerChecks
    private enum FilterMode { None, ShowSelected, ColorSelected }
    private enum PauseMode { Off, Selected }
    private FilterMode _filterMode = FilterMode.None;
    private PauseMode _pauseMode = PauseMode.Selected;   // 預設依勾選等待（Everyone 全勾→等同原「逐句停」行為）
    // 勾選面板（Everyone＋各原子說話人＋(no speaker)）：字幕篩選/顯示與影片等待共用同一份勾選
    private readonly System.Collections.ObjectModel.ObservableCollection<SpeakerCheck> _speakerChecks = new();
    private SpeakerCheck? _everyoneCheck;                // Everyone 列（全選/全清；亦為「等同全部」判準）
    private SpeakerCheck? _noSpeakerCheck;               // (no speaker) 列（未標示句）；無未標示句時不建
    private bool _syncingChecks;                         // Everyone↔各列連動時抑制遞迴
    private readonly HashSet<string> _checkedNames = new(StringComparer.OrdinalIgnoreCase); // 已勾之原子說話人（快取，供每句比對）
    private Dictionary<string, string> _speakerColorHex = new(StringComparer.OrdinalIgnoreCase); // 原子說話人→主題色 hex（無主題色則空）

    private const string NoSpeaker = "(no speaker)";
    private const string EveryoneSpeaker = "(all speakers)"; // 全選列（括號式，避與逐字稿真有「Everyone」說話人撞名；#189-checklist）

    private const string HostName = "lingoisland.player"; // WebView2 虛擬主機：以真實 https origin 供 player.html（避 YouTube Error 150/153 之 null/opaque-origin 內嵌拒絕）
    private static readonly string PlayerCacheBust = Guid.NewGuid().ToString("N"); // 每次啟動唯一：WebView2 依 URL 快取 player.html，同名檔會餵**舊 JS**（改動/更新後不生效）——故 player.html 帶此唯一碼為檔名，保證載當前版本

    /// <summary>暫停句點選單字＝查該字（App 導向獨立字典視窗，沿用 spec#1 查詢）。</summary>
    public event Action<string>? WordLookupRequested;

    /// <summary>加入我的筆記（目前句原文；App 重譯後入既有 NotesStore）。</summary>
    public event Action<string>? AddToNotesRequested;

    public VideoCapturePage(VideoStore videoStore, ThemeStore themes, SubtitleStore subtitles, ITranscriptAligner aligner)
    {
        InitializeComponent();
        _videoStore = videoStore;
        _themes = themes;
        _subs = subtitles;
        _aligner = aligner;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _poll.Tick += OnPoll;
        _cueClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _cueClickTimer.Tick += (_, _) => { _cueClickTimer.Stop(); if (_cueClickWasSelected) { _ = TogglePlayPauseAsync(); } }; // 單擊逾時未等到雙擊：已選中之句→播/暫停切換；未選中者僅選取（不動作）

        // 獲得（epic #178 增量6′「輸入 pivot」）：單一輸入框貼「影片網址＋字幕檔網址」→抽兩網址、走載入管線建立字幕（跑前確認費用）。空框顯範例佔位。
        AcqBuildBtn.Click += (_, _) => DoAcquireBuild();
        AcqInputBox.TextChanged += (_, _) => AcqPlaceholder.Visibility =
            string.IsNullOrEmpty(AcqInputBox.Text) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        // 送出鍵（USR 回饋「按 Enter 沒反應」）：**Enter 直接送出**（符合舊單行框習慣；貼上是 Ctrl+V、不觸發 Enter，故不會誤送）；**Shift+Enter 才插入換行**。Ctrl+Enter 亦送出（Ctrl 不帶 Shift）。
        // **必須用 PreviewKeyDown（穿隧）**：多行 TextBox（AcceptsReturn）於自身 KeyDown 類別處理即插入換行並標 Handled，冒泡 KeyDown 之實例處理器收不到／已太晚——須在穿隧階段搶先攔下才擋得住換行並改為送出。
        AcqInputBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { DoAcquireBuild(); e.Handled = true; }
        };
        // 右側子頁籤（#177 版面重整）：搜尋下載 / 播放學習，以可見性切換（WebView2 不被卸載重建）
        SubTabSearch.Checked += (_, _) => ShowSubTab(showSearch: true);
        SubTabPlay.Checked += (_, _) => ShowSubTab(showSearch: false);
        ReplayBtn.Click += (_, _) => _ = ReplayCurrentAsync();
        ResumeBtn.Click += (_, _) => _ = ResumeAsync();
        NextBtn.Click += (_, _) => _ = SkipNextAsync();
        AddNoteBtn.Click += (_, _) => AddCurrent();
        // 字幕清單：單擊＝選取（點**已選中**之句才播/暫停切換）；**雙擊＝跳到該句起點並暫停顯示畫面**。以系統雙擊時間延後判定單擊、雙擊到達即取消單擊。
        CueList.PreviewMouseLeftButtonDown += (_, _) => { _cueClickWasSelected = CueRowUnderMouse() is CueRow r && ReferenceEquals(r, CueList.SelectedItem); }; // 按下當下（WPF 選取尚未變）記該句是否已選中
        CueList.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _cueClickTimer.Stop();
            if (_cueDoubleClicking) { _cueDoubleClicking = false; return; } // 雙擊之第二次放開：不再武裝單擊計時（跳轉暫停後不得被 toggle 續播）
            _cueClickTimer.Start();
        };
        CueList.MouseDoubleClick += (_, _) => { _cueDoubleClicking = true; _cueClickWasSelected = false; _cueClickTimer.Stop(); _ = JumpToSelectedAsync(); }; // 雙擊＝跳轉暫停；一併解除單擊武裝，防跳轉後 toggle 續播
        // 右鍵選單（#189）：Copy line＋快速指定/修正說話人（依游標下之列動態填入）
        CueList.ContextMenu = new System.Windows.Controls.ContextMenu();
        CueList.ContextMenuOpening += OnCueContextMenuOpening;
        // 說話人顯示模式（無篩選／只顯示勾選／粗體+顏色）＋整檔 YAML 編修（epic #145 增量5／6；#189-checklist）
        SpeakerFilter.SelectionChanged += (_, _) => { if (!_populatingModes) { ApplyFilterMode(); } };
        EditYamlBtn.Click += (_, _) => EnterYamlEdit();
        OffsetApplyBtn.Click += (_, _) => ApplyOffset();                                                          // 增量6′-B：整份字幕時間偏移校正
        OffsetBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyOffset(); e.Handled = true; } };   // Enter 即套用偏移
        PauseAtSpeaker.SelectionChanged += (_, _) => { if (!_populatingModes) { ApplyPauseMode(); } };           // 等待模式（不等待／依勾選）（#189-checklist）
        SpeakerChecks.ItemsSource = _speakerChecks;                                                               // 說話人勾選面板（篩選/顯示/等待共用）
        ApplyYamlBtn.Click += (_, _) => _ = ApplyYamlEditAsync();
        CancelYamlBtn.Click += (_, _) => CancelYamlEdit();
        Loaded += async (_, _) => await EnsureWebAsync();
        IsVisibleChanged += OnVisibleChanged; // 切走分頁：停輪詢＋暫停播放；切回：恢復輪詢

        // 影片清單（epic #145 增量4）＋依 theme 篩選（B）：點清單載入該片、篩選、初次載入
        VideoList.SelectionChanged += OnVideoSelect;
        ClearVideosBtn.Click += (_, _) => OnClearVideos(); // #165 清空影片清單
        OpenFolderBtn.Click += (_, _) => OpenVideoFolder(); // #189：開啟目前影片資料夾（其 subtitle.yaml 與 🌐 Script 逐字稿原文）
        VideoThemeFilter.SelectionChanged += (_, _) => { if (!_populatingVideoFilter) { RefreshVideoList(); } };
        VideoThemePicker.SelectionChanged += (_, _) => { if (!_populatingVideoPicker) { OnVideoThemePicked(); } }; // 內容區塊改指派所屬主題（#173）
        // 刪除改右鍵選單/Delete 鍵（#167，取代 Delete 按鈕）
        VideoList.ContextMenu = ListDeleteSupport.DeleteMenu(OnDeleteVideo);
        VideoList.PreviewMouseRightButtonDown += ListDeleteSupport.SelectItemUnderMouse;
        VideoList.KeyDown += (_, e) => { if (e.Key == Key.Delete) { OnDeleteVideo(); } };
        PopulateVideoThemeFilter();
        RefreshVideoList();
        ApplySubtitleDisplay();   // 字幕帶字級/粗體偏好（比照筆記，設定可調）
        MigrateSubtitleStorage();  // #189：一次性把舊版扁平 subtitles\{id}.json 搬到 video\{日期 標題}\ 結構
    }

    /// <summary>一次性搬遷舊字幕存放（#189）：以影片清單之標題／加入時間，把舊版 <c>subtitles\{id}.json</c> 搬到 <c>video\{yyyyMMdd 標題}\</c>；失敗盡力、不致命。</summary>
    private void MigrateSubtitleStorage()
    {
        try
        {
            var vids = new Dictionary<string, (string Title, string AddedAt)>(StringComparer.Ordinal);
            foreach (var it in _videoStore.Load().Items) { vids[it.VideoId] = (it.Title, it.AddedAt); }
            _subs.MigrateLegacy(vids);
        }
        catch { /* 搬遷不致命 */ }
    }

    /// <summary>套用字幕帶顯示偏好（字級/粗體，比照筆記「條目顯示」）：建立與設定變更時套用。當前句逐字 Run 繼承 SubtitleBand 之字級/字重。</summary>
    public void ApplySubtitleDisplay()
    {
        SubtitleBand.FontSize = SubtitleDisplaySettings.FontSize;
        SubtitleBand.FontWeight = SubtitleDisplaySettings.Bold
            ? System.Windows.FontWeights.Bold
            : System.Windows.FontWeights.Normal;
    }

    /// <summary>切走 Video 分頁即停輪詢並暫停播放（免背景永久 100ms 輪詢與音訊續播）；切回恢復輪詢（不自動續播，由使用者 Continue）。</summary>
    private void OnVisibleChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            _poll.Stop();
            if (_webReady) { try { _ = Web.ExecuteScriptAsync("window.li_pause&&window.li_pause()"); } catch { /* 離場盡力 */ } }
        }
        else
        {
            PopulateVideoThemeFilter(); // 切回本頁重填篩選（反映主題增刪改，B）
            RefreshVideoList();
            if (_guiding && _webReady) { _poll.Start(); }
        }
    }

    private async Task EnsureWebAsync()
    {
        if (_webReady) return;
        _webInit ??= InitWebAsync(); // 單次初始化：Loaded 與 Load 併發僅跑一次（UI 執行緒單線程、??= 免鎖）
        await _webInit;
    }

    private async Task InitWebAsync()
    {
        try
        {
            // autoplay 政策放寬：導引播放需程式化 playVideo() 自動起播（無 WebView2 內文件手勢）。
            var opts = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, null, opts);
            await Web.EnsureCoreWebView2Async(env);

            // player.html 以真實 https 虛擬主機供給——YouTube IFrame 拒 null/opaque origin（NavigateToString）之內嵌（Error 150/153）。
            var dir = Path.Combine(Path.GetTempPath(), "lingoisland-player");
            Directory.CreateDirectory(dir);
            // 檔名帶每次啟動唯一碼：WebView2 依 URL 快取，同名 player.html 會餵舊 JS（改動不生效）——唯一路徑保證載當前版本。清舊檔避免累積。
            try { foreach (var old in Directory.EnumerateFiles(dir, "player-*.html")) { File.Delete(old); } } catch { /* 清理盡力 */ }
            await File.WriteAllTextAsync(Path.Combine(dir, $"player-{PlayerCacheBust}.html"), PlayerHtml());
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HostName, dir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            _webReady = true;
        }
        catch (Exception ex)
        {
            _webInit = null; // 允許下次 Load 再試（runtime 缺失多為永久，但暫時性錯誤可回復）
            SetStatus("WebView2 runtime unavailable — install the Microsoft Edge WebView2 Runtime. (" + ex.Message + ")");
        }
    }

    /// <summary>由 YouTube 連結或 11 碼影片 ID 取出影片 ID；無法辨識回 null。internal 供單元測試。</summary>
    internal static string? ExtractVideoId(string? input)
    {
        var s = (input ?? "").Trim();
        if (Regex.IsMatch(s, @"^[A-Za-z0-9_-]{11}$")) return s;
        var m = Regex.Match(s, @"(?:v=|youtu\.be/|/embed/|/shorts/|/live/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 載入指定影片（epic #178 增量5′ pivot）：已存字幕→直接載入（免費）；未存→**取字幕檔→整理說話人＋台詞→Whisper 取時間→AI 逐句對齊**建立字幕
    /// （會花費、跑前確認、模態顯進度），存檔後導引播放。字幕檔網址取自 finder 存入之 <see cref="_statusStore"/>（增量6′ 由輸入框另提供）；無字幕檔則提示先配字幕檔。
    /// <paramref name="addToStore"/>＝true 時建立成功後加入影片清單（自搜尋結果載入）；點清單載入者已在清單、不重加。
    /// </summary>
    private async Task LoadVideoAsync(string id, bool addToStore, string? listItemId = null)
    {
        var cached = _subs.TryLoad(id); // #174：已存字幕優先（免重建、保留說話人/YAML 編修）
        IReadOnlyList<SubtitleCue>? fresh = null;
        if (cached is null)
        {
            // 未存＝首次載入（epic #178 增量6′-B 定案）：取字幕檔→解析（**字幕檔自帶時間＋說話人,不對齊/不 Whisper/不抓 YouTube 字幕**）→摘要確認。
            // 全在**拆除目前畫面前**做（免費、即時）；使用者取消、或字幕檔無時間碼→保留目前影片。
            var subtitleUrl = _statusStore.Get(id)?.TranscriptUrl;
            if (string.IsNullOrWhiteSpace(subtitleUrl))
            {
                SetStatus("This video has no subtitle-file URL yet — add it under the Acquire tab.");
                if (listItemId is not null) { RefreshVideoList(); } // 審查修：還原清單選取到實際目前影片（提前 return 不錯位到未載入之片）
                return;
            }
            fresh = await FetchParseSubtitleAsync(subtitleUrl); // 內部設狀態；null＝取檔/解析失敗或無時間碼→中止
            if (fresh is null)
            {
                if (listItemId is not null) { RefreshVideoList(); }
                return;
            }
            if (!ConfirmLoadSummary(fresh)) // 顯示摘要（句數/說話人/時長）供一眼確認對得上；取消＝不載入、不拆除目前畫面
            {
                if (listItemId is not null) { RefreshVideoList(); }
                return;
            }
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();

        // 審查修：**確認會實際載入後**才推進清單選取/主題選擇器（清單點選路徑）——否則上方提前 return 會使選取錯位、播放仍舊片。
        if (listItemId is not null) { _currentVideoItemId = listItemId; UpdateVideoThemePicker(); }

        SetLoading(true);
        _guiding = false; _poll.Stop();
        _lastPausedIndex = -1; _shownCue = -1; _playbackStarted = false;
        _currentTitle = null; // 增量6：新片重置標題（起播後自播放器重新取得）
        _currentVideoId = id;  // 字幕存檔鍵（#174）
        SetWatchUrl(id);       // #177：內容區塊顯示可點超連結網址
        SetVideoTitle(_videoStore.Load().Items.FirstOrDefault(i => i.VideoId == id)?.Title ?? id); // #187：先用清單標題/id，起播後以實名更新
        ClearCues();
        SubtitleBand.Inlines.Clear();
        SetControls(false);

        try
        {
            IReadOnlyList<SubtitleCue> cues;
            if (cached is not null) // #174：用已存字幕（免費）
            {
                SetStatus("Loading saved subtitles…");
                cues = cached.Cues;
            }
            else // 首次：已於上方取檔＋解析＋摘要確認（fresh 必非空且含時間碼）→直接存檔載入
            {
                cues = SubtitleParser.NormalizeOrder(fresh!); // 依時間排序後再存（增量6′-B 修）：磁碟 yaml 亦單調,不留回退段
                _subs.Save(id, CurrentVideoTitle(), isAutoGenerated: false, cues); // 存解析結果（#174；自帶時間＋說話人）
            }
            SetCues(cues);
            await EnsureWebAsync();
            if (_webReady)
            {
                Web.CoreWebView2.Navigate($"https://{HostName}/player-{PlayerCacheBust}.html?v={id}");
                _guiding = true;
                _poll.Start();
                if (addToStore) // 自搜尋結果載入＝加入影片清單（依 VideoId 去重、記錄使用中主題）；標題先用 id、起播後自播放器更新
                {
                    var a = ThemeStore.GetActive(_themes.Load());
                    var vi = _videoStore.Add(id, id, a?.Id, a?.Name, DateTimeOffset.Now);
                    _currentVideoItemId = vi.Id;
                    RefreshVideoList();
                }
                // #186：不自動播放。就緒訊息延到 OnPoll 確認播放器 ready 才顯（避免可嵌入被禁/無效影片時謊報）。
                SetStatus($"{_cues.Count} subtitle lines ready — loading player…");
            }
            else
            {
                SetStatus($"{_cues.Count} subtitle lines loaded, but the player is unavailable (WebView2 runtime missing).");
            }
        }
        catch (OperationCanceledException) { SetStatus("Load canceled."); }
        catch (SubtitleException ex) { SetStatus(ex.Message); }
        catch (Exception ex) { SetStatus("Failed to load: " + ex.Message); }
        finally { SetLoading(false); }
    }

    /// <summary>
    /// 取字幕檔並解析為逐句 cue（epic #178 增量6′-B「時間 pivot」定案）：字幕檔**自帶時間＋說話人,直接用**——不對齊、不 Whisper、不亂序。
    /// (1) **免費解析**：curl 取檔（繞 Cloudflare）→ <see cref="SubtitleParser.Parse"/>（VTT/SRT 箭頭）→ <see cref="SubtitleParser.ParseTimedTranscript"/>（fandom 式 HH:MM:SS 逐字稿）→ <see cref="SubtitleParser.ExtractInlineSpeakers"/>（NAME: 前綴）。多數常見來源即此免費路徑。
    /// (2) 版面五花八門、免費解析抽不到時間 → 跑前確認費用後,以 AI **直接抽取**（<see cref="ITranscriptAligner.ExtractTimedCuesAsync"/>；照網頁自己的時間戳、非猜/對齊,故不亂序）。取檔/解析失敗/取消/仍無時間→設狀態、回 null。
    /// </summary>
    private async Task<IReadOnlyList<SubtitleCue>?> FetchParseSubtitleAsync(string subtitleUrl)
    {
        string raw;
        try
        {
            SetStatus("Reading the subtitle file…");
            raw = await TranscriptFetch.FetchAsync(subtitleUrl, CancellationToken.None);
        }
        catch (OperationCanceledException) { SetStatus("Canceled."); return null; }
        catch (SubtitleException ex) { SetStatus(ex.Message); return null; }
        catch (Exception ex) { SetStatus("Couldn't read the subtitle file: " + ex.Message); return null; }

        // (1) 免費解析：VTT/SRT（「-->」箭頭）→ fandom 式「HH:MM:SS 說話人： 台詞」→ 補抽 NAME: 說話人。抽到時間就直接用（免費、即時）。
        var parsed = SubtitleParser.Parse(raw);
        if (!parsed.Any(c => c.StartSec.HasValue)) { parsed = SubtitleParser.ParseTimedTranscript(raw); }
        var det = SubtitleParser.ExtractInlineSpeakers(parsed);
        if (det.Any(c => c.StartSec.HasValue)) { return det; }

        // (2) 免費解析讀不到時間（五花八門的版面）→ 請 AI 讀整頁抽取（照網頁自己的時間戳,非猜/對齊）。花費、跑前確認。
        if (!ConfirmAiExtract()) { SetStatus("Kept the current video — AI extraction canceled."); return null; }
        IReadOnlyList<SubtitleCue>? aiCues = null;
        AiActionWindow.RunAndShow(System.Windows.Window.GetWindow(this), "Extract subtitles with AI",
            async (report, ct) =>
            {
                var progress = new System.Progress<string>(s => report(s));
                var res = await _aligner.ExtractTimedCuesAsync(TranscriptAlign.StripToPlainText(raw), progress, ct);
                if (res.Truncated)
                {
                    report("This page is too long to extract in one pass — try a shorter transcript page.");
                    return TranscriptCost(res.Usages);
                }
                var extracted = SubtitleParser.ExtractInlineSpeakers(res.Cues);
                if (!extracted.Any(c => c.StartSec.HasValue))
                {
                    report("The AI didn't find timestamps on this page — check the URL is a timed transcript.");
                    return TranscriptCost(res.Usages);
                }
                aiCues = extracted;
                report($"Extracted {extracted.Count} line(s); {extracted.Count(c => c.StartSec.HasValue)} timed.");
                return TranscriptCost(res.Usages);
            },
            autoCloseOnSuccess: false, showCost: true);
        if (aiCues is null) { SetStatus("Couldn't extract timed subtitles from that page — see the dialog."); return null; }
        return aiCues;
    }

    /// <summary>
    /// 載入前摘要確認（epic #178 增量6′-B）：顯示解析出的句數／已定時句數／時長跨度／說話人清單,供使用者**一眼確認「這份字幕對得上這支影片」**;按 OK 才載入。
    /// 無 AI、無費用——「內容是否對得上」以人眼看摘要取代 AI 猜（AI 猜正是一直出包處）。無說話人則於摘要標明（仍可載入、只是不能指定說話人暫停）。
    /// </summary>
    private bool ConfirmLoadSummary(IReadOnlyList<SubtitleCue> cues)
    {
        var timed = cues.Count(c => c.StartSec.HasValue);
        var speakers = cues.Select(c => c.Speaker).Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lastSec = cues.Where(c => c.StartSec.HasValue).Max(c => c.StartSec!.Value);
        var speakerLine = speakers.Count > 0
            ? $"說話人（{speakers.Count}）：{string.Join("、", speakers.Take(8))}{(speakers.Count > 8 ? "…" : "")}"
            : "⚠ 這份字幕沒有說話人標記（仍可載入,但無法指定說話人暫停）";
        var msg =
            $"字幕檔解析結果：\n\n{cues.Count} 句 · {timed} 句有時間 · 涵蓋 0:00–{FormatPos(lastSec)}\n{speakerLine}\n\n" +
            "確認這份字幕對得上這支影片,載入嗎？（載入免費；時間可到內容頁用偏移量微調）";
        return System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this), msg, "Load subtitles",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;
    }

    /// <summary>把 <see cref="SpeakerUsage"/> 轉為對話視窗費用顯示用之 <see cref="AiActionWindow.AiUsage"/> 清單（AI 抽取路徑之費用呈現）。</summary>
    private static List<AiActionWindow.AiUsage> TranscriptCost(IReadOnlyList<SpeakerUsage> usages)
        => usages.Select(u => new AiActionWindow.AiUsage(u.InputTokens, u.OutputTokens, u.Model, u.WebSearch)).ToList();

    /// <summary>AI 直接抽取跑前確認（epic #178 增量6′-B）：僅免費解析讀不到時間之網頁才需 AI；顯示流程與粗估費用,按 OK 才花費。強調時間照網頁原樣抄、非估算/對齊（故不亂序）。</summary>
    private bool ConfirmAiExtract()
    {
        var msg =
            "這個字幕頁不是標準字幕檔格式,免費解析讀不到時間。要用 AI 讀整頁、逐句抽出「時間＋說話人＋台詞」嗎？\n" +
            "（AI 只照抄網頁上原有的時間戳,不推算、不對齊——所以不會亂序。）\n\n" +
            "估算費用：約 NT$1–3 一頁（僅一次；之後同片載入免費）。使用你的 OpenAI 金鑰。\n" +
            "帳戶餘額請於 platform.openai.com/usage 查看。\n\n" +
            "要用 AI 抽取嗎？";
        return System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this), msg, "Extract subtitles with AI",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;
    }

    // ---- 影片清單（epic #145 增量4） ----

    /// <summary>重載影片清單（標題／加入時間／主題名）；目前載入影片自動選中（不觸發載入）。空清單顯提示。</summary>
    public void RefreshVideoList()
    {
        var d = _videoStore.Load();
        var themeId = ThemeFilter.SelectedThemeId(VideoThemeFilter); // null＝All（B）
        var shown = d.Items.Where(it => ThemeFilter.Match(themeId, it.ThemeId)).ToList();
        VideoList.SelectionChanged -= OnVideoSelect;
        VideoList.Items.Clear();
        foreach (var it in shown)
        {
            VideoList.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = VideoItemView(it),
                Tag = it,
                Padding = new System.Windows.Thickness(4),
            });
        }
        if (_currentVideoItemId is not null)
        {
            for (int i = 0; i < VideoList.Items.Count; i++)
            {
                if ((VideoList.Items[i] as System.Windows.Controls.ListBoxItem)?.Tag is VideoItem v && v.Id == _currentVideoItemId)
                {
                    VideoList.SelectedIndex = i;
                    break;
                }
            }
        }
        VideoList.SelectionChanged += OnVideoSelect;
        VideoEmptyHint.Text = d.Items.Count == 0
            ? "No videos yet. Add one under the Acquire tab."
            : "No videos for this theme."; // 有影片但本 theme 無
        VideoEmptyHint.Visibility = shown.Count > 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        UpdateVideoThemePicker(); // 與清單/標題/篩選同步：顯示目前影片之所屬主題（#173）
    }

    /// <summary>以目前主題重填「依 theme 篩選」下拉（圖文）；期間抑制重整、保留選取。</summary>
    private void PopulateVideoThemeFilter()
    {
        _populatingVideoFilter = true;
        ThemeFilter.Populate(VideoThemeFilter, _themes);
        _populatingVideoFilter = false;
    }

    // ---- 內容區塊「所屬主題」下拉（#173）：顯示目前影片之主題、改選即重指派 ----

    /// <summary>以目前影片之所屬主題重填「所屬主題」下拉並啟用；未載入影片則清空並停用。期間抑制 SelectionChanged→重指派。</summary>
    private void UpdateVideoThemePicker()
    {
        _populatingVideoPicker = true;
        if (_currentVideoItemId is null)
        {
            VideoThemePicker.Items.Clear();
            VideoThemePicker.IsEnabled = false;
        }
        else
        {
            var it = _videoStore.Load().Items.FirstOrDefault(i => i.Id == _currentVideoItemId);
            ThemeFilter.PopulatePicker(VideoThemePicker, _themes, it?.ThemeId);
            VideoThemePicker.IsEnabled = it is not null;
        }
        _populatingVideoPicker = false;
    }

    /// <summary>「所屬主題」改選→回寫目前影片之主題（名稱取自現行主題清單）；重整清單反映主題名／依 theme 篩選。</summary>
    private void OnVideoThemePicked()
    {
        if (_currentVideoItemId is null) { return; }
        var id = ThemeFilter.PickedThemeId(VideoThemePicker);
        var name = id is null ? null : ThemeStore.Find(_themes.Load(), id)?.Name;
        _videoStore.UpdateTheme(_currentVideoItemId, id, name);
        RefreshVideoList(); // 反映清單主題名／篩選（目前片仍以 _currentVideoItemId 選中或落選）
        RebuildSpeakerColors();  // 主題改變→重算說話人自動配色（#189-checklist）
        RefreshCueEmphasis();    // 粗體+顏色模式立即反映新主題色
    }

    private System.Windows.Controls.StackPanel VideoItemView(VideoItem it)
    {
        var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        sp.Children.Add(MakeThumb(it.VideoId, 56, 36)); // YouTube 縮圖（#171，比照主題清單）
        var col = new System.Windows.Controls.StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        col.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = it.Title,
            FontSize = 12.5,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
            Foreground = System.Windows.Media.Brushes.DarkSlateGray,
        });
        var meta = FormatTime(it.AddedAt) + (string.IsNullOrWhiteSpace(it.ThemeName) ? "" : "  ·  " + it.ThemeName);
        col.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = meta,
            FontSize = 10.5,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x6A, 0x82)),
        });
        sp.Children.Add(col);
        return sp;
    }

    /// <summary>YouTube 縮圖 Image（#171）：以 <c>img.youtube.com/vi/{id}/default.jpg</c> 遠端載入；離線/失敗留空、不致命。</summary>
    private static System.Windows.Controls.Image MakeThumb(string videoId, double w, double h)
    {
        var img = new System.Windows.Controls.Image
        {
            Width = w,
            Height = h,
            Stretch = System.Windows.Media.Stretch.UniformToFill,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        img.Source = MakeThumbSource(videoId);
        return img;
    }

    /// <summary>YouTube 縮圖 ImageSource（<c>img.youtube.com/vi/{id}/default.jpg</c>）；離線/失敗回 null（不致命）。清單項與搜尋結果表格共用（#177）。</summary>
    private static System.Windows.Media.ImageSource? MakeThumbSource(string videoId)
    {
        try { return new System.Windows.Media.Imaging.BitmapImage(new Uri($"https://img.youtube.com/vi/{videoId}/default.jpg")); }
        catch { return null; }
    }

    private static string FormatTime(string iso) =>
        DateTimeOffset.TryParse(iso, out var t) ? t.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : iso;

    // ---- 獲得（epic #178 增量6′「輸入 pivot」）：單一輸入框貼「影片網址＋字幕檔網址」→抽兩網址、走載入管線建立字幕 ----

    /// <summary>
    /// 獲得（epic #178 增量6′「輸入 pivot」）：使用者於單一輸入框貼含**具體 YouTube 影片網址＋字幕檔網址**之自然語言文字。
    /// 以純字串抽出兩個網址（**不做關鍵字搜尋**——要求具體 URL、避免不穩的 AI 配片與鬼打牆），記字幕檔網址到該片、走載入管線建立字幕
    /// （取字幕檔→整理說話人＋台詞→Whisper 取時間→逐句對齊；跑前確認費用）、加入內容頁。抽不到影片／字幕檔網址即以狀態列明確回報所缺、不動作、不花費。
    /// </summary>
    private void DoAcquireBuild()
    {
        var urls = ExtractUrls(AcqInputBox.Text);
        // 影片＝第一個能取出 YouTube 影片 ID 之網址；字幕＝第一個**非** YouTube 影片之 http(s) 網址。兩者皆須具體給定。
        string? id = null;
        foreach (var u in urls) { id = ExtractVideoId(u); if (id is not null) { break; } }
        var subtitleUrl = urls.FirstOrDefault(u => ExtractVideoId(u) is null);
        if (id is null)
        {
            SetStatus("Add a YouTube video URL — for example https://www.youtube.com/watch?v=…");
            return;
        }
        if (string.IsNullOrWhiteSpace(subtitleUrl))
        {
            SetStatus("Add a subtitle-file URL too — a transcript page that names the speakers (for example a fandom transcript).");
            return;
        }
        // 已有此片紀錄→先問覆寫重置或取消（增量6′-B）：覆寫＝清掉已存字幕強制重建；取消＝保留現有、不動作、不花費。
        if (_subs.TryLoad(id) is not null)
        {
            var overwrite = System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this),
                "這支影片已有字幕紀錄。要覆寫、重新建立嗎？（會重新花費建立；取消則保留現有紀錄）",
                "Video already added",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
            if (overwrite != System.Windows.MessageBoxResult.OK) { SetStatus("Kept the existing subtitles for this video."); return; }
            _subs.Remove(id); // 清已存字幕→LoadVideoAsync 走未快取（重建）
        }
        // 記字幕檔網址到該片：LoadVideoAsync 未快取時即取此建立字幕（快取還原與載入來源一致）。
        _statusStore.SaveWeb(id, found: true, source: "Direct input", transcriptUrl: subtitleUrl);
        _ = LoadVideoAsync(id, addToStore: true); // 未快取→確認費用→建立管線；成功加入影片清單並切到內容頁
    }

    /// <summary>由自由文字抽出所有 http(s) 網址（epic #178 增量6′）：允許 URL 內含括號（維基式有配對 <c>(</c>），但去除 markdown 連結尾之孤立 <c>)</c>；並去常見尾標點。供獲得框抽取影片＋字幕檔網址。internal 供單元測試。</summary>
    internal static IReadOnlyList<string> ExtractUrls(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) { return list; }
        // 僅取 RFC3986 之 URL 字元（ASCII）；自然停在 CJK（如「，」「字幕」）與空白，免把中文說明黏進網址。
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, @"https?://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+"))
        {
            var u = m.Value.TrimEnd('.', ',', ';', ':', '!', '?'); // 去常見句尾標點（皆合法 URL 字元、故先納入再修尾）
            while (u.EndsWith(")", StringComparison.Ordinal) && !u.Contains('(')) { u = u.Substring(0, u.Length - 1); } // markdown 連結尾之孤立 )（維基式含配對 ( 者不動）
            if (u.Length > 10) { list.Add(u); } // 長於 "https://"（8）才算有效 URL 主體
        }
        return list;
    }


    /// <summary>表格點連結→於系統預設瀏覽器開 YouTube 原頁（沿用 AboutPage 開連結作法）。</summary>
    private void OnOpenExternalLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Could not open link: " + ex.Message); }
        e.Handled = true;
    }

    /// <summary>開啟**目前影片**之資料夾（#189，📁）：檔案總管開 <c>video\{yyyyMMdd 標題}\</c>（含其 subtitle.yaml 與 🌐 Script 逐字稿原文）；未載入影片則開字幕根目錄。</summary>
    private void OpenVideoFolder()
    {
        try
        {
            var dir = _currentVideoId is not null ? _subs.FolderFor(_currentVideoId, CurrentVideoTitle()) : SubtitleStore.DefaultRoot;
            if (string.IsNullOrEmpty(dir)) { dir = SubtitleStore.DefaultRoot; }
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Could not open the video folder: " + ex.Message); }
    }

    /// <summary>目前影片之最佳已知標題（供字幕存檔資料夾命名，#189）：優先起播後取得之實名，退清單標題，再退影片 ID；無影片回空。</summary>
    private string CurrentVideoTitle()
    {
        if (!string.IsNullOrWhiteSpace(_currentTitle)) { return _currentTitle!; }
        var id = _currentVideoId;
        if (id is null) { return ""; }
        var listed = _videoStore.Load().Items.FirstOrDefault(i => i.VideoId == id)?.Title;
        return string.IsNullOrWhiteSpace(listed) ? id : listed!;
    }

    private void OnVideoSelect(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var it = (VideoList.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as VideoItem;
        if (it is null || it.Id == _currentVideoItemId) return; // 無選取或已是目前載入 → 不重載
        // 審查修：_currentVideoItemId／主題選擇器改於 LoadVideoAsync **確認會實際載入後**才推進——
        // 否則未載入若於摘要確認取消／無字幕檔網址而提前 return，選取會錯位到新片、播放仍舊片。
        _ = LoadVideoAsync(it.VideoId, addToStore: false, listItemId: it.Id); // 已在清單、不重加
    }

    private void OnDeleteVideo()
    {
        var it = (VideoList.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as VideoItem;
        if (it is null) return;
        var deletedIndex = VideoList.SelectedIndex; // 於目前（篩選後）清單之位置（#175）
        _videoStore.Remove(it.Id);
        _subs.Remove(it.VideoId); // #174：連同刪其已存字幕
        if (it.Id == _currentVideoItemId) _currentVideoItemId = null;
        RefreshVideoList();

        // #175：刪除後主內容顯示「上一筆」（清單中被刪項之上一筆）；無上一筆則清空
        var prevIndex = deletedIndex - 1;
        if (prevIndex >= 0 && prevIndex < VideoList.Items.Count)
        {
            VideoList.SelectedIndex = prevIndex; // 觸發 OnVideoSelect → 載入上一筆
        }
        else
        {
            ClearContentArea(); // 無上一筆 → 內容區塊為空
        }
    }

    /// <summary>清空影片清單（#165 Clear all，含確認）；不影響目前載入中之播放。</summary>
    private void OnClearVideos()
    {
        if (_videoStore.Load().Items.Count == 0) return;
        if (System.Windows.MessageBox.Show("Remove all videos from the list?", "Clear videos",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK)
        {
            return;
        }
        _videoStore.Clear();
        _subs.Clear();          // #174：連同清空所有已存字幕
        ClearContentArea();     // #175：清空後內容區塊為空
        RefreshVideoList();
    }

    /// <summary>清空主內容區塊（#175：刪至無上一筆／Clear all）：取消進行中載入、停播放器（about:blank）、清字幕與控制、停用主題下拉。</summary>
    private void ClearContentArea()
    {
        _loadCts?.Cancel(); // 取消進行中載入，避免其完成後又蓋回內容（AI 動作為模態、不會併發於此）
        _currentVideoItemId = null;
        _currentVideoId = null;
        _guiding = false; _poll.Stop();
        _lastPausedIndex = -1; _shownCue = -1; _playbackStarted = false;
        _currentTitle = null;
        if (_webReady) { try { Web.CoreWebView2.Navigate("about:blank"); } catch { /* 盡力清空播放器 */ } }
        ClearCues();
        SubtitleBand.Inlines.Clear();
        SetControls(false);
        WatchUrlRow.Visibility = System.Windows.Visibility.Collapsed; // #177：無載入→隱藏超連結網址
        VideoTitleText.Visibility = System.Windows.Visibility.Collapsed; // #187：無載入→隱藏標題
        UpdateVideoThemePicker(); // _currentVideoItemId null → 停用
        SetStatus("No video loaded. Find one by transcript under Acquire.");
    }

    /// <summary>起播後自 YouTube 播放器取標題、回寫影片清單項（epic #145 增量4）。</summary>
    private async Task UpdateCurrentVideoTitleAsync()
    {
        if (_currentVideoItemId is null || !_webReady) return;
        try
        {
            var raw = await Web.ExecuteScriptAsync("window.li_title?window.li_title():''");
            var title = JsonSerializer.Deserialize<string>(raw) ?? "";
            if (!string.IsNullOrWhiteSpace(title))
            {
                _currentTitle = title;                 // 增量6：供 AI 說話人推斷輔助判斷角色
                SetVideoTitle(title);                  // #187：以播放器實名更新內容區塊標題
                _videoStore.UpdateTitle(_currentVideoItemId, title);
                RefreshVideoList();
            }
        }
        catch { /* 取標題失敗維持 id 為標題 */ }
    }

    /// <summary>承載 YouTube IFrame Player API 之最小 HTML（自 query 之 <c>v</c> 取影片 ID）；宿主以 li_time/li_err/li_pause/li_play/li_seek 控制。
    /// 經 WebView2 虛擬主機以真實 https origin 供給——NavigateToString 之 null/opaque origin 會被 YouTube 拒（Error 150/153）。
    /// onError 記錄錯誤碼（可嵌入被禁 101/150、已移除 100、參數錯 2 等）供宿主明確降級。</summary>
    private static string PlayerHtml() => """
<!doctype html><html><head><meta charset="utf-8">
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}#p{width:100%;height:100%}</style></head>
<body><div id="p"></div>
<script>
var player,ready=false,lastErr=-1,seekPausePending=false;
var vid=new URLSearchParams(location.search).get('v')||'';
var tag=document.createElement('script');tag.src="https://www.youtube.com/iframe_api";
document.head.appendChild(tag);
function onYouTubeIframeAPIReady(){player=new YT.Player('p',{height:'100%',width:'100%',videoId:vid,
 playerVars:{'playsinline':1,'rel':0,'modestbranding':1,'origin':location.origin},
 events:{'onReady':function(){ready=true;},
         'onStateChange':function(e){if(seekPausePending&&e.data==1){player.pauseVideo();}},
         'onError':function(e){lastErr=e.data;}}});}
window.li_time=function(){return (ready&&player&&player.getCurrentTime)?player.getCurrentTime():-1;};
window.li_title=function(){return (ready&&player&&player.getVideoData)?(player.getVideoData().title||''):'';};
window.li_err=function(){return lastErr;};
window.li_pause=function(){if(ready&&player)player.pauseVideo();};
window.li_play=function(){if(ready&&player){seekPausePending=false;player.playVideo();}};
window.li_toggle=function(){if(ready&&player){seekPausePending=false;var s=(player.getPlayerState?player.getPlayerState():-1);if(s==1){player.pauseVideo();}else{player.playVideo();}}};
window.li_seek=function(t){if(ready&&player){seekPausePending=false;player.seekTo(t,true);player.playVideo();}};
window.li_seek_pause=function(t){if(ready&&player){seekPausePending=true;player.seekTo(t,true);player.playVideo();if(window._sp)clearInterval(window._sp);var k=0;window._sp=setInterval(function(){if(!seekPausePending||!player){clearInterval(window._sp);window._sp=null;return;}var s=(player.getPlayerState?player.getPlayerState():-1);var now=(player.getCurrentTime?player.getCurrentTime():t);if(s==1||now>t+0.03){player.pauseVideo();}if(++k>=40){clearInterval(window._sp);window._sp=null;}},50);}};
</script></body></html>
""";

    private async void OnPoll(object? sender, EventArgs e)
    {
        if (!_webReady || !_guiding || _cues.Count == 0) return;
        if (_polling) return; // 重入防護：橋接往返 >100ms 時免並發跑動 _lastPausedIndex
        _polling = true;
        try
        {
            // 播放器錯誤（可嵌入被禁／已移除／地區封鎖等）→ 明確降級、停止導引，不謊報成功。
            var errRaw = await Web.ExecuteScriptAsync("window.li_err?window.li_err():-1");
            if (int.TryParse((errRaw ?? "").Trim('"'), out var err) && err >= 0)
            {
                _guiding = false; _poll.Stop();
                SetStatus($"This video can't be played here (YouTube error {err}) — it may be embedding-disabled or unavailable. Try another video.");
                return;
            }

            var raw = await Web.ExecuteScriptAsync("window.li_time?window.li_time():-1");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) return;
            if (t < 0) return;

            if (!_playbackStarted) // 播放器就緒（#186：不自動播放）→ 顯首句、啟用控制、提示按 Continue 開始
            {
                _playbackStarted = true;
                SubTabPlay.IsChecked = true;        // #178 增量6′-B USR：確認播放器就緒（載入成功）才切到內容頁——不先跳
                _ = UpdateCurrentVideoTitleAsync(); // epic #145 增量4：就緒後自播放器取標題回寫影片清單
                ShowCue(0);                         // 顯示第一句＋啟用控制鈕（不自動播放）
                SetStatus($"{_cues.Count} lines loaded — press ▶ Continue to play (pauses at each line), or double-click a line to jump there (paused).");
            }

            // 播放時字幕清單跟隨（#178 增量6′-B USR）：當前句隨播放時間前進即高亮＋捲動＋更新字幕帶（不只在暫停點）。
            var cur = PauseDecider.CueAt(t, _cues);
            if (cur >= 0 && cur != _shownCue) { ShowCue(cur); }

            if (PauseTargets() is { } pt) // 等待模式＝依勾選（#189-checklist）：不等待模式回 null→只跟隨、不暫停
            {
                var pause = PauseDecider.NextPause(t, _cues, _lastPausedIndex, pauseSpeakers: pt.Targets, pauseNoSpeaker: pt.NoSpeaker); // 勾選之說話人（含未標示）才暫停；Everyone 全勾→逐句停
                if (pause >= 0)
                {
                    _lastPausedIndex = pause;
                    try { await Web.ExecuteScriptAsync("window.li_pause&&window.li_pause()"); } catch { /* 盡力暫停 */ }
                    ShowCue(pause);
                }
            }
        }
        catch { /* 輪詢盡力，橋接偶發例外不致命 */ }
        finally { _polling = false; }
    }

    private void ShowCue(int i)
    {
        if (i < 0 || i >= _rows.Count) return;
        _shownCue = i;
        SetControls(true); // 控制鈕於首次顯示字幕（自動暫停或點清單跳播）後才生效，避免暫停前空點無反應
        var row = _rows[i]; // _rows 依原始順序建立，Index 與 _cues 對齊
        if (!ReferenceEquals(CueList.SelectedItem, row)) { CueList.SelectedItem = row; } // 程式化選取；CueList 無 SelectionChanged 處理器（跳播走滑鼠事件），不需抑制旗標
        CueList.ScrollIntoView(row);     // 若當前句被說話人篩選濾掉則不捲動（正常，字幕帶仍顯示）
        RenderClickable(_cues[i]);
    }

    /// <summary>字幕與筆記條目同色（比照筆記 #3A2C33）；說話人前綴另加粗。凍結共用、免每句重配。</summary>
    private static readonly System.Windows.Media.SolidColorBrush EntryTextBrush = MakeFrozen(0x3A, 0x2C, 0x33);
    private static System.Windows.Media.SolidColorBrush MakeFrozen(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    /// <summary>把字幕句以逐字可點呈現（說話人前置粗體、非可點；單字＝Hyperlink→WordLookupRequested；分隔＝純文字），沿用 EnglishWordTokenizer。色比照筆記條目。</summary>
    /// <summary>固定字幕說話人前綴（#189）：「名: 」;未知（空/空白）一律標「unknown: 」。字幕帶與清單共用,格式一致、不再混亂。</summary>
    private static string SpeakerLabelOf(string? speaker) =>
        (string.IsNullOrWhiteSpace(speaker) ? "unknown" : speaker) + ": ";

    /// <summary>字幕清單時間標（#189）：cue 起始秒→「m:ss」，超過一小時→「h:mm:ss」；負值視為 0。</summary>
    private static string FormatPos(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    /// <summary>右鍵「Copy line」複製目前字幕帶那一句台詞（#189；字幕帶字可點查詞，故以右鍵複製取代拖選）。</summary>
    private void OnCopySubtitleLine(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_shownCue >= 0 && _shownCue < _cues.Count) { TrySetClipboard(_cues[_shownCue].Text); }
    }

    /// <summary>
    /// 字幕清單右鍵選單（#189）：依游標下之列動態填入——Copy line＋「Set speaker」快速指定/修正說話人
    /// （既有說話人清單一鍵套用＋清除為 unknown＋自訂新名）。無列或 YAML 編修中則不顯。
    /// </summary>
    private void OnCueContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        var cm = CueList.ContextMenu!;
        cm.Items.Clear();
        var row = CueRowUnderMouse();
        if (row is null || _yamlEditing) { e.Handled = true; return; } // 無列/編修中→不顯選單

        var copy = new System.Windows.Controls.MenuItem { Header = "Copy line" };
        copy.Click += (_, _) => TrySetClipboard(row.Cue.Text);
        cm.Items.Add(copy);
        cm.Items.Add(new System.Windows.Controls.Separator());
        cm.Items.Add(new System.Windows.Controls.MenuItem { Header = "Set speaker", IsEnabled = false }); // 標題列（停用）
        foreach (var name in _cues.Where(c => !string.IsNullOrEmpty(c.Speaker)).Select(c => c.Speaker!)
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var captured = name;
            var mi = new System.Windows.Controls.MenuItem
            {
                Header = "  " + name,
                IsChecked = string.Equals(name, row.Cue.Speaker, StringComparison.OrdinalIgnoreCase),
            };
            mi.Click += (_, _) => AssignSpeaker(row, captured);
            cm.Items.Add(mi);
        }
        var clear = new System.Windows.Controls.MenuItem
        {
            Header = "  (unknown / clear)",
            IsChecked = string.IsNullOrEmpty(row.Cue.Speaker),
        };
        clear.Click += (_, _) => AssignSpeaker(row, null);
        cm.Items.Add(clear);
        var neu = new System.Windows.Controls.MenuItem { Header = "  New speaker…" };
        neu.Click += (_, _) => { var n = PromptSpeakerName(row.Cue.Speaker); if (n is not null) { AssignSpeaker(row, n); } };
        cm.Items.Add(neu);
    }

    /// <summary>游標下的字幕列（右鍵選單用）：自 <see cref="System.Windows.Input.Mouse.DirectlyOver"/> 上溯至 ListBoxItem 取其 <see cref="CueRow"/>。</summary>
    private CueRow? CueRowUnderMouse()
    {
        var dep = System.Windows.Input.Mouse.DirectlyOver as System.Windows.DependencyObject;
        while (dep is not null and not System.Windows.Controls.ListBoxItem)
        {
            dep = VisualOrLogicalParent(dep);
        }
        return (dep as System.Windows.Controls.ListBoxItem)?.DataContext as CueRow;
    }

    /// <summary>取父節點，可跨 Visual 與 ContentElement：點在字幕之 <c>Run</c>（ContentElement，非 Visual）上時，
    /// <see cref="System.Windows.Media.VisualTreeHelper.GetParent"/> 會擲「not a Visual」——故 Visual/Visual3D 走視覺樹、其餘（Run/Inline 等）走邏輯樹（Run 之邏輯父為所屬 TextBlock，回到視覺樹）。</summary>
    private static System.Windows.DependencyObject? VisualOrLogicalParent(System.Windows.DependencyObject d)
        => d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(d)
            : System.Windows.LogicalTreeHelper.GetParent(d);

    /// <summary>快速指定/修正某句說話人（#189）：改該句 Speaker（null／空＝清除為 unknown）、**就地更新該列**（不重建清單→不跳捲軸）、存檔、同步篩選/暫停下拉，當前句則重繪字幕帶。</summary>
    private void AssignSpeaker(CueRow row, string? speaker)
    {
        var i = row.Index;
        if (i < 0 || i >= _cues.Count) { return; }
        var clean = string.IsNullOrWhiteSpace(speaker) ? null : speaker.Trim();
        if (string.Equals(_cues[i].Speaker, clean, StringComparison.Ordinal)) { return; } // 無變化
        var list = _cues.ToList();
        list[i] = list[i] with { Speaker = clean };
        _cues = list;
        row.UpdateSpeaker(clean);                                                   // 就地通知 UI（該列 SpeakerLabel 更新）
        if (_currentVideoId is not null) { _subs.Save(_currentVideoId, CurrentVideoTitle(), isAutoGenerated: false, list); } // 存回（#174；增量5′ 已無 Auto/Manual 之分）
        if (_shownCue == i) { RenderClickable(_cues[i]); }                          // 當前句→重繪字幕帶（含新說話人前綴）
        PopulateSpeakerChecks();                                                    // 新說話人可能出現/消失→重建勾選面板（保留原勾選）＋同步配色
        RefreshFilterView();                                                        // 篩選/顯示依新說話人重整
        RefreshCueEmphasis();
        SetStatus(clean is null ? $"Cleared speaker on line {i + 1}." : $"Set line {i + 1} speaker to “{clean}”.");
    }

    /// <summary>「New speaker…」文字輸入（#189）：小型模態輸入框；取消回 null（呼叫端不動作）、OK 回輸入字（空白＝清除）。</summary>
    private string? PromptSpeakerName(string? initial)
    {
        var win = new System.Windows.Window
        {
            Title = "Speaker name",
            Width = 300,
            SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Window.GetWindow(this),
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Enter a speaker name:", Margin = new System.Windows.Thickness(0, 0, 0, 6) });
        var tb = new System.Windows.Controls.TextBox { Text = initial ?? "" };
        panel.Children.Add(tb);
        var btns = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 10, 0, 0) };
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 68, IsDefault = true, Margin = new System.Windows.Thickness(0, 0, 6, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 68, IsCancel = true };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; win.DialogResult = true; };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);
        win.Content = panel;
        win.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return win.ShowDialog() == true ? result : null;
    }

    /// <summary>寫入剪貼簿（剪貼簿被占用等失敗時靜默忽略，不擲例外）。</summary>
    private static void TrySetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return; }
        try { System.Windows.Clipboard.SetText(text); } catch { /* 剪貼簿暫被占用等——忽略 */ }
    }

    private void RenderClickable(SubtitleCue cue)
    {
        SubtitleBand.Inlines.Clear();
        // 固定格式「說話人：內容」（#189）：說話人一律前置、粗體、非可點；未知標 unknown（不再有無說話人就不顯示的混亂）
        SubtitleBand.Inlines.Add(new Run(SpeakerLabelOf(cue.Speaker))
        {
            FontWeight = System.Windows.FontWeights.Bold, // 說話人名稱標粗體（#字幕說話人標粗體）
            Foreground = EntryTextBrush,                  // 與筆記條目同色
        });
        foreach (var tok in EnglishWordTokenizer.Tokenize(cue.Text))
        {
            if (tok.IsWord)
            {
                var word = tok.Text;
                var link = new Hyperlink(new Run(word))
                {
                    Foreground = EntryTextBrush, // 字幕顏色比照筆記條目 #3A2C33（可點仍以游標手勢示意）
                    Cursor = System.Windows.Input.Cursors.Hand,
                    TextDecorations = null,
                };
                link.Click += (_, _) => WordLookupRequested?.Invoke(word);
                SubtitleBand.Inlines.Add(link);
            }
            else
            {
                SubtitleBand.Inlines.Add(new Run(tok.Text));
            }
        }
    }

    private void AddCurrent()
    {
        if (_shownCue >= 0 && _shownCue < _cues.Count)
        {
            var t = _cues[_shownCue].Text;
            if (t.Length > 0) AddToNotesRequested?.Invoke(t);
        }
    }

    private async Task ReplayCurrentAsync()
    {
        if (_shownCue < 0 || _shownCue >= _cues.Count || !_webReady) return;
        if (_cues[_shownCue].StartSec is not double sec) return; // #184：未定時句無已知時間可跳，安全略過
        _lastPausedIndex = _shownCue - 1; // 允許重播後於本句結束再暫停
        await SeekAsync(sec);
    }

    private async Task ResumeAsync()
    {
        if (!_webReady) return;
        try { await Web.ExecuteScriptAsync("window.li_play&&window.li_play()"); } catch { /* 盡力續播 */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime(); // 系統雙擊判定時間（ms），供區分字幕清單單擊/雙擊

    /// <summary>字幕清單單擊**已選中**之句：切換播放/暫停（依播放器實際狀態）；未就緒／無字幕／YAML 編修中則忽略。</summary>
    private async Task TogglePlayPauseAsync()
    {
        if (!_webReady || _cues.Count == 0 || _yamlEditing) { return; }
        try { await Web.ExecuteScriptAsync("window.li_toggle&&window.li_toggle()"); } catch { /* 盡力切換 */ }
    }

    private async Task SkipNextAsync()
    {
        if (_cues.Count == 0 || !_webReady) return;
        var next = _shownCue + 1;
        if (next >= _cues.Count) return;
        _lastPausedIndex = next - 1;
        ShowCue(next);
        if (_cues[next].StartSec is double sec) await SeekAsync(sec); // #184：未定時句仍顯示，但無法 seek 定位
    }

    /// <summary>雙擊字幕句→**跳到該句起點、暫停並顯示該處畫面**（只定位不續播；之後按 ▶ Continue 才自該句起播、到句末暫停）。</summary>
    private async Task JumpToSelectedAsync()
    {
        if (CueList.SelectedItem is not CueRow row || !_webReady) return;
        var i = row.Index;
        if (i < 0 || i >= _cues.Count) return;
        _lastPausedIndex = i - 1; // 之後 Continue＝自此句起點播、到句末暫停（不影響本次「只定位不播」）
        ShowCue(i);
        if (_cues[i].StartSec is double sec) await SeekPauseAsync(sec); // #184：未定時句仍顯示，但無法跳轉定位
    }

    /// <summary>跳到指定秒並播放（Replay／Next 用）。</summary>
    private async Task SeekAsync(double sec)
    {
        try { await Web.ExecuteScriptAsync($"window.li_seek&&window.li_seek({sec.ToString(CultureInfo.InvariantCulture)})"); }
        catch { /* 盡力跳播 */ }
    }

    /// <summary>跳到指定秒並**暫停**（#189：字幕點跳只定位、不自動播放）。</summary>
    private async Task SeekPauseAsync(double sec)
    {
        try { await Web.ExecuteScriptAsync($"window.li_seek_pause&&window.li_seek_pause({sec.ToString(CultureInfo.InvariantCulture)})"); }
        catch { /* 盡力定位 */ }
    }

    // ---- 說話人字幕：CueRow 綁定、依說話人篩選、整檔 YAML 編修（epic #145 增量5，#154） ----

    /// <summary>設定目前字幕：建 CueRow（保留原始 index）、綁 CueList＋套說話人篩選檢視、重填說話人下拉、啟用工具列。</summary>
    private void SetCues(IReadOnlyList<SubtitleCue> cues)
    {
        // 依開始時間穩定排序（增量6′-B 修）:PauseDecider／CueAt 要求 cues 遞增,而逐字稿頁常見場景倒敘/片尾曲致時間回退
        // （USR「選 Ryder 沒每次停」病根）。載入、時間偏移、YAML 編修**三條安裝路徑皆經此**→單點保證單調。對已遞增輸入 idempotent。
        _cues = SubtitleParser.NormalizeOrder(cues);
        _rows = new List<CueRow>(_cues.Count);
        for (var i = 0; i < _cues.Count; i++) _rows.Add(new CueRow(i, _cues[i]));
        CueList.ItemsSource = _rows;
        _cueView = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);
        _cueView.Filter = CueRowFilter;
        PopulateSpeakerChecks();   // 重建說話人勾選面板（保留原勾選）＋同步主題配色
        SyncModeSelectors();       // 下拉選取反映目前模式
        RefreshFilterView();       // 依模式套篩選
        RefreshCueEmphasis();      // 依模式套粗體+顏色
        var has = _cues.Count > 0;
        SetSpeakerControlsEnabled(has);
        EditYamlBtn.IsEnabled = has;
        SyncOffsetEnabled(has); // #187 有字幕載入；增量5′：已帶說話人則停用（見方法）
    }

    /// <summary>同步時間偏移列（增量6′-B「時間 pivot」）：有字幕且**至少一句已定時**才可校正——全未定時（無對齊）則平移無意義、停用。</summary>
    private void SyncOffsetEnabled(bool baseEnabled)
    {
        var on = baseEnabled && _cues.Any(c => c.StartSec.HasValue);
        OffsetBox.IsEnabled = on;
        OffsetApplyBtn.IsEnabled = on;
    }

    /// <summary>清空字幕與清單、關工具列（載入新片／取消時）。編修中則先退出編修 UI。</summary>
    private void ClearCues()
    {
        if (_yamlEditing) ExitYamlEditUi();
        _cues = new List<SubtitleCue>();
        _rows = new List<CueRow>();
        CueList.ItemsSource = null;
        _cueView = null;
        // 清空勾選面板（解除訂閱免殘留）＋回預設模式（無篩選／依勾選等待）
        _populatingModes = true;
        foreach (var sc in _speakerChecks) sc.PropertyChanged -= OnSpeakerCheckPropChanged;
        _speakerChecks.Clear(); _everyoneCheck = null; _noSpeakerCheck = null; _checkedNames.Clear();
        _populatingModes = false;
        _filterMode = FilterMode.None; _pauseMode = PauseMode.Selected;
        SyncModeSelectors();
        SetSpeakerControlsEnabled(false);
        EditYamlBtn.IsEnabled = false;
        OffsetBox.IsEnabled = false; OffsetApplyBtn.IsEnabled = false;    // 增量6′-B：清空時停用時間偏移列
    }

    // ---- 說話人勾選面板（#189-checklist USR）：Everyone＋各原子說話人（合唸句拆開去重）＋(no speaker)；篩選/顯示/等待共用 ----

    /// <summary>
    /// 重建說話人勾選面板：Everyone＋各**原子**說話人（把「Ryder and Marshall」拆成 Ryder／Marshall 去重排序）＋（有未標示句時）「(no speaker)」。
    /// 保留原勾選狀態（依名字；新名預設勾）；首次全勾。同步 _checkedNames 快取與主題自動配色。清單/顯示/等待統一由呼叫端刷新。
    /// </summary>
    private void PopulateSpeakerChecks()
    {
        // 保留原勾選（依名字；重建後同名沿用、新增預設勾）
        var prevChecked = _speakerChecks.Count > 0
            ? new HashSet<string>(_speakerChecks.Where(s => s.IsChecked).Select(s => s.Name), StringComparer.OrdinalIgnoreCase)
            : null;
        bool WasChecked(string name) => prevChecked is null || prevChecked.Contains(name);

        _populatingModes = true;
        foreach (var sc in _speakerChecks) sc.PropertyChanged -= OnSpeakerCheckPropChanged;
        _speakerChecks.Clear();
        _everyoneCheck = null; _noSpeakerCheck = null;

        var atoms = _cues.Where(c => !string.IsNullOrEmpty(c.Speaker))
                         .SelectMany(c => PauseDecider.SplitSpeakers(c.Speaker))     // 合唸句拆為個別名字
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        var hasNoSpeaker = _cues.Any(c => string.IsNullOrEmpty(c.Speaker));

        if (atoms.Count > 0 || hasNoSpeaker)
        {
            _everyoneCheck = AddCheck(new SpeakerCheck(EveryoneSpeaker, isEveryone: true));
            foreach (var a in atoms) AddCheck(new SpeakerCheck(a) { IsChecked = WasChecked(a) });
            if (hasNoSpeaker) _noSpeakerCheck = AddCheck(new SpeakerCheck(NoSpeaker, isNoSpeaker: true) { IsChecked = WasChecked(NoSpeaker) });
            _everyoneCheck.IsChecked = _speakerChecks.Where(x => !x.IsEveryone).All(x => x.IsChecked); // 全勾才勾 Everyone
        }
        _populatingModes = false;

        RebuildCheckedNames();
        RebuildSpeakerColors();

        SpeakerCheck AddCheck(SpeakerCheck sc) { sc.PropertyChanged += OnSpeakerCheckPropChanged; _speakerChecks.Add(sc); return sc; }
    }

    /// <summary>勾選變更：Everyone↔各列連動；重算已勾快取；暫停自目前時間重算；刷新篩選檢視與強調（粗體+顏色）。</summary>
    private void OnSpeakerCheckPropChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_populatingModes || _syncingChecks || sender is not SpeakerCheck sc) return;
        _syncingChecks = true;
        if (sc.IsEveryone)
        {
            foreach (var other in _speakerChecks) if (!other.IsEveryone) other.IsChecked = sc.IsChecked; // 全選/全清
        }
        else if (_everyoneCheck is not null)
        {
            _everyoneCheck.IsChecked = _speakerChecks.Where(x => !x.IsEveryone).All(x => x.IsChecked);    // 全部個別勾→Everyone 勾
        }
        _syncingChecks = false;

        RebuildCheckedNames();
        _lastPausedIndex = -1;      // 勾選變→暫停判定自目前時間重算
        RefreshFilterView();
        RefreshCueEmphasis();
    }

    /// <summary>依勾選面板重算「已勾原子說話人」快取（供每句比對，免每次掃 ObservableCollection）。</summary>
    private void RebuildCheckedNames()
    {
        _checkedNames.Clear();
        foreach (var sc in _speakerChecks)
            if (sc.IsChecked && !sc.IsEveryone && !sc.IsNoSpeaker) _checkedNames.Add(sc.Name);
    }

    /// <summary>某句之說話人是否被勾選（未標示句看 (no speaker) 勾選；具名句看其拆出之任一原子名是否在已勾集合）。</summary>
    private bool SpeakerChecked(string? cueSpeaker)
    {
        if (string.IsNullOrEmpty(cueSpeaker)) return _noSpeakerCheck?.IsChecked == true;
        foreach (var a in PauseDecider.SplitSpeakers(cueSpeaker)) if (_checkedNames.Contains(a)) return true;
        return false;
    }

    /// <summary>粗體+顏色模式下某句之背景色 hex（該句第一個「已勾且有配色」之原子說話人色）；無則 null（只粗體、不上色）。</summary>
    private string? ColorForSpeaker(string? cueSpeaker)
    {
        if (string.IsNullOrEmpty(cueSpeaker)) return null;
        foreach (var a in PauseDecider.SplitSpeakers(cueSpeaker))
            if (_checkedNames.Contains(a) && _speakerColorHex.TryGetValue(a, out var hex)) return hex;
        return null;
    }

    /// <summary>依現用主題之 ColorRules 色盤，自動輪派給各原子說話人（USR：自動配色）。主題無定義顏色→留空→僅粗體、不上色。</summary>
    private void RebuildSpeakerColors()
    {
        _speakerColorHex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var theme = CurrentTheme();
        var hexes = theme?.ColorRules is { Count: > 0 } rules
            ? NoteColors.Palette.Where(p => rules.TryGetValue(p.Name, out var d) && !string.IsNullOrWhiteSpace(d)).Select(p => p.Hex).ToList()
            : new List<string>();
        if (hexes.Count == 0) return; // 主題無配色→只粗體
        var atoms = _speakerChecks.Where(sc => !sc.IsEveryone && !sc.IsNoSpeaker).Select(sc => sc.Name).ToList();
        for (var i = 0; i < atoms.Count; i++) _speakerColorHex[atoms[i]] = hexes[i % hexes.Count];
    }

    /// <summary>目前內容頁所選之主題（供自動配色取 ColorRules）；未指派→null。</summary>
    private LingoIsland.Query.ThemeItem? CurrentTheme()
    {
        var id = ThemeFilter.PickedThemeId(VideoThemePicker);
        return id is null ? null : ThemeStore.Find(_themes.Load(), id);
    }

    /// <summary>刷新每列強調（粗體+顏色）：僅「粗體+顏色」模式且該句說話人被勾選時，套主題色背景＋台詞加粗；其餘還原正常。</summary>
    private void RefreshCueEmphasis()
    {
        var color = _filterMode == FilterMode.ColorSelected;
        foreach (var row in _rows)
        {
            var on = color && SpeakerChecked(row.Cue.Speaker);
            row.SetEmphasis(on, on ? ColorForSpeaker(row.Cue.Speaker) : null);
        }
    }

    /// <summary>重整清單檢視（套 CueRowFilter）。</summary>
    private void RefreshFilterView() => _cueView?.Refresh();

    /// <summary>清單篩選：僅「只顯示勾選者」模式才隱藏未勾之句；「無篩選」「粗體+顏色」皆全顯示。</summary>
    private bool CueRowFilter(object o)
    {
        if (o is not CueRow row) return true;
        if (_filterMode != FilterMode.ShowSelected) return true;
        return SpeakerChecked(row.Cue.Speaker);
    }

    /// <summary>顯示模式下拉改變（0 無篩選／1 只顯示勾選／2 粗體+顏色）→重整檢視與強調。</summary>
    private void ApplyFilterMode()
    {
        _filterMode = SpeakerFilter.SelectedIndex switch { 1 => FilterMode.ShowSelected, 2 => FilterMode.ColorSelected, _ => FilterMode.None };
        RefreshFilterView();
        RefreshCueEmphasis();
    }

    /// <summary>等待模式下拉改變（0 不等待／1 依勾選等待）→暫停自目前時間重算。</summary>
    private void ApplyPauseMode()
    {
        _pauseMode = PauseAtSpeaker.SelectedIndex == 1 ? PauseMode.Selected : PauseMode.Off;
        _lastPausedIndex = -1;
    }

    /// <summary>把下拉選取同步為目前模式（不觸發套用）。</summary>
    private void SyncModeSelectors()
    {
        _populatingModes = true;
        SpeakerFilter.SelectedIndex = _filterMode switch { FilterMode.ShowSelected => 1, FilterMode.ColorSelected => 2, _ => 0 };
        PauseAtSpeaker.SelectedIndex = _pauseMode == PauseMode.Selected ? 1 : 0;
        _populatingModes = false;
    }

    /// <summary>啟/停用說話人相關控制（顯示模式下拉＋等待模式下拉＋勾選面板）。</summary>
    private void SetSpeakerControlsEnabled(bool on)
    {
        SpeakerFilter.IsEnabled = on;
        PauseAtSpeaker.IsEnabled = on;
        SpeakerChecks.IsEnabled = on;
    }

    /// <summary>
    /// 等待對象（#189-checklist）：不等待模式→null（OnPoll 不暫停）；Everyone 全勾→(null,false)＝非指定（句末停、原逐句學習行為）；
    /// 否則→(已勾原子集合, 是否含未標示)＝指定對象（於該句起點停；空集合→無人符合→不停）。
    /// </summary>
    private (IReadOnlyCollection<string>? Targets, bool NoSpeaker)? PauseTargets()
    {
        if (_pauseMode == PauseMode.Off) return null;
        if (_everyoneCheck?.IsChecked == true) return (null, false);
        return (_checkedNames, _noSpeakerCheck?.IsChecked == true);
    }

    /// <summary>
    /// 套用時間偏移（epic #178 增量6′-B「時間 pivot」）：把偏移框之 <c>MM:SS</c>（可負）整體平移全部字幕時間、存回字幕存檔、重整清單與播放判定。
    /// 慣例：**字幕快→輸負**（如 <c>-00:05</c> 使字幕延後 5 秒對上發音）、**慢→輸正**——即 <c>新時間 = 原時間 − 輸入值</c>（見 <see cref="ShiftCues"/>）。
    /// 空框／0／格式錯→狀態列提示、不動；無已定時句→無可平移、忽略。套用後框歸零供下次再微調。純平移、不改斷句/說話人。
    /// </summary>
    private void ApplyOffset()
    {
        if (_cues.Count == 0 || _yamlEditing || _loading || _currentVideoId is null) { return; }
        var secs = ParseOffsetSeconds(OffsetBox.Text);
        if (secs is null) { SetStatus("Enter a time offset (MM:SS or seconds). Positive = subtitles later, negative = earlier — e.g. 00:03 or -00:05."); return; }
        if (secs.Value == 0) { SetStatus("Offset is 0:00 — no change."); return; }
        if (!_cues.Any(c => c.StartSec.HasValue)) { SetStatus("These subtitles have no timing to shift."); return; }

        // 逐句改時間＋存檔＋重繪需一兩秒→沙漏游標,免使用者混亂（USR）。首次偏移前保留原始時間檔（資料夾同時留原始與校正後兩檔，USR）。
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            _subs.BackupOriginalOnce(_currentVideoId, CurrentVideoTitle());
            var shifted = ShiftCues(_cues, secs.Value); // 直接加：正＝延後、負＝提前（USR 修正：與原先相反）
            _lastPausedIndex = -1;                       // 時間全平移→暫停判定自目前時間重新起算
            SetCues(shifted);
            _subs.Save(_currentVideoId, CurrentVideoTitle(), isAutoGenerated: false, shifted); // 存回校正後（#174）
            if (_rows.Count > 0) { ShowCue(0); }
        }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        OffsetBox.Text = "00:00";                        // 歸零供下次再微調
        var dir = secs.Value < 0 ? "earlier" : "later";
        SetStatus($"Shifted all subtitles {Math.Abs(secs.Value):0.##}s {dir}. Adjust again if needed.");
    }

    /// <summary>把每句開始時間平移 <paramref name="deltaSeconds"/> 秒（純函式，internal 供單元測試）：已定時句 <c>+delta</c>（不低於 0）、未定時句（null）保持 null；斷句/說話人不動。</summary>
    internal static IReadOnlyList<SubtitleCue> ShiftCues(IReadOnlyList<SubtitleCue> cues, double deltaSeconds)
        => cues.Select(c => c.StartSec is double s
                ? c with { StartSec = Math.Round(Math.Max(0, s + deltaSeconds), 3) }
                : c).ToList();

    /// <summary>
    /// 解析時間偏移輸入為秒（純函式，internal 供單元測試）：接受 <c>MM:SS</c>／<c>SS</c>、可帶前導 <c>-</c>／<c>+</c>；
    /// 例 <c>-00:05</c>→-5、<c>1:30</c>→90、<c>-7</c>→-7、<c>00:00</c>→0。空白／格式錯／負號非於最前／逾 <c>MM:SS</c>→null（呼叫端提示）。
    /// </summary>
    internal static double? ParseOffsetSeconds(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) { return null; }
        var sign = 1.0;
        if (t[0] == '-') { sign = -1.0; t = t[1..].Trim(); }
        else if (t[0] == '+') { t = t[1..].Trim(); }
        if (t.Length == 0) { return null; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var parts = t.Split(':');
        if (parts.Length == 1)
        {
            return double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out var only) && only >= 0 ? sign * only : null;
        }
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, inv, out var mm) || mm < 0) { return null; }
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var ss) || ss < 0) { return null; }
            return sign * (mm * 60 + ss);
        }
        return null; // HH:MM:SS 以上不支援（偏移一般 < 1 分）
    }

    /// <summary>進入整檔 YAML 編修：序列化目前字幕入編輯框、停導引＋暫停播放、切換清單→編輯面板。</summary>
    private void EnterYamlEdit()
    {
        if (_cues.Count == 0 || _yamlEditing) return;
        _guiding = false; _poll.Stop();
        if (_webReady) { try { _ = Web.ExecuteScriptAsync("window.li_pause&&window.li_pause()"); } catch { /* 盡力暫停 */ } }
        YamlBox.Text = SubtitleYaml.Serialize(_cues);
        _yamlEditing = true;
        CuePanel.Visibility = System.Windows.Visibility.Collapsed;   // 連同說話人勾選面板一併隱藏（#189-checklist）
        YamlEditor.Visibility = System.Windows.Visibility.Visible;
        SetSpeakerControlsEnabled(false);
        EditYamlBtn.IsEnabled = false;
        OffsetBox.IsEnabled = false; OffsetApplyBtn.IsEnabled = false; // 增量6′-B：YAML 編修期間停用時間偏移
        SetStatus("Editing subtitle as YAML — merge/split lines and set speakers, then Apply.");
    }

    /// <summary>套用 YAML 編修：解析→取代字幕；解析失敗（含 YAML 語法錯）留在編修模式明訊。續從目前播放時間到句暫停。</summary>
    private async Task ApplyYamlEditAsync()
    {
        if (!_yamlEditing) return;
        IReadOnlyList<SubtitleCue> parsed;
        try { parsed = SubtitleYaml.Parse(YamlBox.Text); }
        catch (SubtitleException ex) { SetStatus(ex.Message); return; } // 留在編修模式供修正
        if (parsed.Count == 0) { SetStatus("No subtitle lines found in the YAML — fix and Apply, or Cancel."); return; }

        ExitYamlEditUi();
        SetCues(parsed);
        if (_currentVideoId is not null) { _subs.Save(_currentVideoId, CurrentVideoTitle(), isAutoGenerated: false, parsed); } // 存 YAML 編修結果（#174；增量5′ 已無 Auto/Manual 之分）

        var t = await CurrentTimeAsync();               // 對齊目前播放時間，續從當前位置（不跳回開頭）
        var at = PauseDecider.CueAt(t, parsed);         // start-only：當前句＝起點<=t 之最後一句
        _lastPausedIndex = Math.Max(-1, at - 1);        // 下一次暫停自當前句起（暫停點＝下一句起點或本句起點＋上限）
        if (at >= 0) ShowCue(at); else { _shownCue = -1; SubtitleBand.Inlines.Clear(); }
        _guiding = _webReady;
        if (_webReady && IsVisible) _poll.Start();
        SetStatus($"{parsed.Count} lines after edit — playback pauses at each line; tap a word to look it up.");
    }

    /// <summary>取消 YAML 編修：丟棄編輯內容、還原清單、恢復導引。</summary>
    private void CancelYamlEdit()
    {
        if (!_yamlEditing) return;
        ExitYamlEditUi();
        var has = _cues.Count > 0;
        SetSpeakerControlsEnabled(has);
        EditYamlBtn.IsEnabled = has;
        SyncOffsetEnabled(has); // #187；增量5′：已帶說話人則停用
        if (_webReady && IsVisible && has) { _guiding = true; _poll.Start(); }
    }

    private void ExitYamlEditUi()
    {
        _yamlEditing = false;
        YamlEditor.Visibility = System.Windows.Visibility.Collapsed;
        CuePanel.Visibility = System.Windows.Visibility.Visible;   // 還原清單＋說話人勾選面板（#189-checklist）
    }

    /// <summary>目前播放秒數（橋接失敗／未起播回 0）。</summary>
    private async Task<double> CurrentTimeAsync()
    {
        if (!_webReady) return 0;
        try
        {
            var raw = await Web.ExecuteScriptAsync("window.li_time?window.li_time():0");
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 0;
        }
        catch { return 0; }
    }

    /// <summary>右側字幕清單一列 view-model：保留原始 cue 於 <see cref="Index"/>（篩選/顯示不動播放 index）；<see cref="Display"/> 說話人前置。</summary>
    private sealed class CueRow : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public CueRow(int index, SubtitleCue cue) { Index = index; Cue = cue; }
        public int Index { get; }
        public SubtitleCue Cue { get; private set; }
        /// <summary>時間標（#189）：cue 起始位置「m:ss」（超過一小時「h:mm:ss」）＋兩空白，置於說話人之前、清單以淡色 Run 呈現。#184：未定時句（null）→ 無時間標（空字串）。</summary>
        public string TimeLabel => Cue.StartSec is double s ? FormatPos(s) + "  " : "";
        /// <summary>說話人前綴（固定「名: 」;未知＝「unknown: 」,#189）——清單以粗體 Run 呈現。</summary>
        public string SpeakerLabel => SpeakerLabelOf(Cue.Speaker);
        /// <summary>台詞文字（清單以正常字重 Run 呈現）。</summary>
        public string Text => Cue.Text;

        // 粗體+顏色強調（#189-checklist）：僅「粗體+顏色」模式且本句說話人被勾選時，背景上主題色、台詞加粗；預設透明/正常＝與原外觀一致。
        private System.Windows.Media.Brush _rowBg = System.Windows.Media.Brushes.Transparent;
        public System.Windows.Media.Brush RowBackground { get => _rowBg; private set { if (!ReferenceEquals(_rowBg, value)) { _rowBg = value; Raise(nameof(RowBackground)); } } }
        private System.Windows.FontWeight _lineWeight = System.Windows.FontWeights.Normal;
        public System.Windows.FontWeight LineWeight { get => _lineWeight; private set { if (_lineWeight != value) { _lineWeight = value; Raise(nameof(LineWeight)); } } }
        /// <summary>設定本列強調：on＝該句被勾選且處於粗體+顏色模式；hex 非 null 時上主題背景色（否則只加粗、不上色，＝主題無配色時之退回）。</summary>
        public void SetEmphasis(bool on, string? hex)
        {
            RowBackground = on && hex is not null ? BrushOfHex(hex) : System.Windows.Media.Brushes.Transparent;
            LineWeight = on ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
        }

        /// <summary>就地更新說話人（#189 右鍵指定）：換 Cue 並通知 SpeakerLabel 變更，使清單該列即時更新、免重建（不跳捲軸）。</summary>
        public void UpdateSpeaker(string? speaker)
        {
            Cue = Cue with { Speaker = speaker };
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SpeakerLabel)));
        }
        private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    // 主題色 hex → 凍結筆刷（快取；色盤固定、跨列共用免重建）。供 CueRow 強調背景。
    private static readonly Dictionary<string, System.Windows.Media.Brush> HexBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static System.Windows.Media.Brush BrushOfHex(string hex)
    {
        if (HexBrushCache.TryGetValue(hex, out var b)) return b;
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var br = new System.Windows.Media.SolidColorBrush(color); br.Freeze();
        HexBrushCache[hex] = br;
        return br;
    }

    /// <summary>說話人勾選面板一列（#189-checklist）：名字＋是否 Everyone／(no speaker)＋勾選態（TwoWay 綁 CheckBox）。Everyone 列加粗以區別。</summary>
    private sealed class SpeakerCheck : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public SpeakerCheck(string name, bool isEveryone = false, bool isNoSpeaker = false) { Name = name; IsEveryone = isEveryone; IsNoSpeaker = isNoSpeaker; }
        public string Name { get; }
        public bool IsEveryone { get; }
        public bool IsNoSpeaker { get; }
        public System.Windows.FontWeight Weight => IsEveryone ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
        private bool _checked = true;
        public bool IsChecked { get => _checked; set { if (_checked != value) { _checked = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked))); } } }
    }

    /// <summary>取目前載入影片之所屬主題名（供 AI 字幕分析輔助）；無載入或未指派主題→null。</summary>
    private string? CurrentThemeName()
    {
        if (_currentVideoItemId is null) { return null; }
        var name = _videoStore.Load().Items.FirstOrDefault(i => i.Id == _currentVideoItemId)?.ThemeName;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>切換右側子頁籤（#177）：搜尋下載／播放學習；以可見性切換——兩 pane 皆留於視覺樹，WebView2 不被卸載重建、播放不中斷。</summary>
    private void ShowSubTab(bool showSearch)
    {
        SearchPane.Visibility = showSearch ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        PlayPane.Visibility = showSearch ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    /// <summary>內容區塊顯示目前影片之可點超連結網址（#177）：設定 Hyperlink 目標與顯示文字並顯示該列。</summary>
    private void SetWatchUrl(string videoId)
    {
        var url = "https://www.youtube.com/watch?v=" + videoId;
        WatchUrlText.Text = url;
        WatchUrlLink.NavigateUri = new Uri(url);
        WatchUrlRow.Visibility = System.Windows.Visibility.Visible;
    }

    // 搜尋縮圖尺寸（#187，選項頁可調）：DataTemplate 之 Image 綁此 DP（AncestorType=UserControl）
    public static readonly System.Windows.DependencyProperty ThumbHeightProperty =
        System.Windows.DependencyProperty.Register(nameof(ThumbHeight), typeof(double), typeof(VideoCapturePage), new System.Windows.PropertyMetadata(36.0));
    public double ThumbHeight { get => (double)GetValue(ThumbHeightProperty); set => SetValue(ThumbHeightProperty, value); }
    public static readonly System.Windows.DependencyProperty ThumbWidthProperty =
        System.Windows.DependencyProperty.Register(nameof(ThumbWidth), typeof(double), typeof(VideoCapturePage), new System.Windows.PropertyMetadata(64.0));
    public double ThumbWidth { get => (double)GetValue(ThumbWidthProperty); set => SetValue(ThumbWidthProperty, value); }

    /// <summary>套用搜尋縮圖高度（#187，選項頁可調 28–120）：設 Thumb 尺寸 DP（寬＝高×16/9）。增量6′ 砍結果表後此 DP 已無綁定，僅保留供選項頁呼叫不致錯。</summary>
    public void ApplyThumbSize(double height)
    {
        var h = Math.Clamp(height, 28, 120);
        ThumbHeight = h;
        ThumbWidth = Math.Round(h * 16.0 / 9.0);
    }

    /// <summary>內容區塊網址之上顯示影片標題（#187）；空則隱藏。</summary>
    private void SetVideoTitle(string? title)
    {
        var t = (title ?? "").Trim();
        VideoTitleText.Text = t;
        VideoTitleText.Visibility = t.Length > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }


    private void SetStatus(string msg) => StatusText.Text = msg;

    private void SetLoading(bool loading)
    {
        _loading = loading;
        // 載入等待動畫（#186）：載入中顯旋轉沙漏、收起 WebView2（避 airspace 遮住 WPF 疊層）
        LoadOverlay.Visibility = loading ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Web.Visibility = loading ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    private void SetControls(bool enabled)
    {
        ReplayBtn.IsEnabled = enabled;
        ResumeBtn.IsEnabled = enabled;
        NextBtn.IsEnabled = enabled;
        AddNoteBtn.IsEnabled = enabled;
    }
}
