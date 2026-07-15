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
/// 影片擷取分頁（[modVideoCapture模組]／[techApp桌面查詢工具] 擷取來源頁，spec#2）：貼 YouTube 影片→
/// yt-dlp 取字幕（<see cref="ISubtitleFetcher"/>）→ WebView2 內嵌 YouTube IFrame Player API 導引播放、
/// <see cref="PauseDecider"/> 到句暫停顯字幕→暫停句逐字可點（<see cref="WordLookupRequested"/>，沿用既有查詢）→
/// 加入既有筆記（<see cref="AddToNotesRequested"/>）。與螢幕擷取並列之可插拔擷取來源、下游完全共用。
/// </summary>
public partial class VideoCapturePage : System.Windows.Controls.UserControl
{
    private readonly ISubtitleFetcher _fetcher;
    private readonly VideoStore _videoStore;                       // 影片清單持久化（epic #145 增量4）
    private readonly ThemeStore _themes;                           // 使用中主題（加入影片時記錄跨媒體歸屬）＋依 theme 篩選（B）＋內容區塊所屬主題指派（#173）
    private bool _populatingVideoFilter;                           // 重填篩選下拉期間抑制 SelectionChanged→重整
    private bool _populatingVideoPicker;                           // 重填「所屬主題」下拉期間抑制 SelectionChanged→重指派（#173）
    private readonly ISpeakerEnricher _enricher;                   // 說話人來源疊加（epic #145 增量6：AI 推斷；會用到 API、按鈕觸發）
    private readonly ISpeakerEnricher _webEnricher;                // 說話人來源（增量6b）：OpenAI 網搜工具上網找逐字稿（會用到 API、按鈕觸發）
    private readonly IWebTranscriptProbe _webProbe;                // 網路字幕可用性探測（#177）：搜尋結果表格「網路字幕」欄按需查（只跑 find 一步、便宜）
    private readonly IVideoSearcher _searcher;                     // 依關鍵字搜尋 YouTube（#171）
    private readonly IAudioTranscriber _transcriber;               // Whisper 音訊轉錄（#187）：抓真實聲音重轉字幕、修時間漂移；會用 API＋下載音訊、按鈕觸發、跑前確認費用
    private readonly ISubtitleRefiner _refiner;                     // #189 Row2「🧠 AI 分析」：LLM 重分句＋標說話人、時間不變（Auto 基底用）；會用 API、按鈕觸發、跑前確認費用
    private readonly SubtitleStore _subs;                          // 字幕存檔：免重抓、保留說話人/YAML 編修（#174）
    private List<SearchRow> _searchRows = new();                   // 搜尋結果表格資料（#177）：縮圖/名稱/連結/片長/三種字幕狀態
    private System.Windows.Data.ListCollectionView? _searchView;   // 可過濾/預設排序之檢視（#177）；點表頭排序由 DataGrid 內建接手
    private readonly SearchHistoryStore _searchHistory = new();     // 搜尋關鍵字歷史（#186）：搜尋框下拉、可刪
    private readonly SearchResultRecordStore _searchRecords = new();// 搜尋成果初次時間紀錄（#186）：First-seen 欄
    private readonly VideoSubtitleStatusStore _statusStore = new();  // 字幕狀態快取（#188）：還原已探測之 Manual/Auto/Web，重搜同片免重探/免重花額度
    private readonly AiSpendLedger _spendLedger = new();             // AI 花費帳本（#189）：每次 AI 動作跑前顯示本日/本小時累計、事後記帳
    private CancellationTokenSource? _searchCts;                   // 搜尋可取消（新搜尋取代進行中者）；亦取消其內嵌字幕探測
    private string? _currentVideoItemId;                           // 目前載入影片於清單之項 Id（供更新標題／選中）
    private string? _currentVideoId;                               // 目前載入影片之 YouTube ID（字幕存檔鍵，#174）
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _cueClickTimer;               // 區分字幕清單單擊（播/暫停切換）與雙擊（跳播）（#173）
    private IReadOnlyList<SubtitleCue> _cues = new List<SubtitleCue>();
    private int _lastPausedIndex = -1; // 上次已暫停之 cue（PauseDecider 用）
    private int _shownCue = -1;        // 目前字幕帶顯示之 cue
    private bool _webReady;
    private Task? _webInit;            // WebView2 單次初始化任務（避 Loaded 與 Load 併發重複 CreateAsync 擲例外）
    private bool _guiding;             // 導引播放中（輪詢到句暫停生效）
    private bool _polling;             // OnPoll 重入防護（async void 掛 100ms timer、橋接往返可能 >100ms）
    private bool _playbackStarted;     // 實際起播確認後才宣稱「逐句暫停」成功（避可嵌入被禁時謊報）
    private bool _isAuto;              // 目前字幕為自動生成（逐字滾動、較破碎）——供狀態提示；亦即字幕來源＝Auto（#189，Row1 純顯示）
    private bool _loading;             // 抓字幕中（LoadBtn 兼作 Cancel）
    private CancellationTokenSource? _loadCts; // 抓字幕可取消（新 Load／取消鈕）

    // 說話人字幕（epic #145 增量5）：CueList 綁 CueRow view-model（保留原始 _cues index，篩選/顯示不動播放 index）
    private List<CueRow> _rows = new();
    private System.ComponentModel.ICollectionView? _cueView; // _rows 之預設檢視，套說話人篩選
    private string? _speakerFilter;    // null＝全部；否則特定說話人名
    private bool _filterNoSpeaker;     // true＝僅顯示未標示說話人之句
    private bool _refreshingCues;      // 重建/篩選/程式化選取期間，抑制 SelectionChanged→跳播
    private bool _yamlEditing;         // 整檔 YAML 編修模式中
    private bool _inferring;           // AI 說話人推斷中（防重入、按鈕停用）（增量6）
    private bool _transcribing;        // Whisper 轉錄中（防重入、按鈕停用）（#187）
    private Row2Method _row2Applied = Row2Method.None; // #189：Row2 已套用之方法（🌐Script／🧠AI）——顯「已按下」；換基底/新片重置
    private bool _row3Applied;         // #189：Row3（🎙Voice 精修時間）是否已套用
    private const string ScriptLabel = "\U0001F310 Script";  // 🌐
    private const string AnalyzeLabel = "\U0001F9E0 AI";     // 🧠
    private const string VoiceLabel = "\U0001F399 Voice";    // 🎙
    private string? _currentTitle;     // 目前影片標題（起播後取得，供 AI 推斷輔助判斷角色）（增量6）
    private string? _pauseSpeaker;     // 指定說話人才暫停（增量7）；null＝全部說話人皆暫停
    private bool _pauseNoSpeaker;      // #189：只在未標示（unknown）之句暫停（Pause-at 選 (no speaker)）
    private bool _populatingPauseAt;   // 重填 Pause-at 下拉期間抑制 SelectionChanged
    private const string AllSpeakers = "All speakers";
    private const string NoSpeaker = "(no speaker)";
    private const string EveryoneSpeaker = "Everyone"; // Pause-at 之「全部」選項（增量7）

    private const string HostName = "lingoisland.player"; // WebView2 虛擬主機：以真實 https origin 供 player.html（避 YouTube Error 150/153 之 null/opaque-origin 內嵌拒絕）

    /// <summary>暫停句點選單字＝查該字（App 導向獨立字典視窗，沿用 spec#1 查詢）。</summary>
    public event Action<string>? WordLookupRequested;

    /// <summary>加入我的筆記（目前句原文；App 重譯後入既有 NotesStore）。</summary>
    public event Action<string>? AddToNotesRequested;

    public VideoCapturePage(ISubtitleFetcher fetcher, VideoStore videoStore, ThemeStore themes,
                            ISpeakerEnricher enricher, ISpeakerEnricher webEnricher, IWebTranscriptProbe webProbe,
                            IVideoSearcher searcher, SubtitleStore subtitles, IAudioTranscriber transcriber,
                            ISubtitleRefiner refiner)
    {
        InitializeComponent();
        _fetcher = fetcher;
        _videoStore = videoStore;
        _themes = themes;
        _enricher = enricher;
        _webEnricher = webEnricher;
        _webProbe = webProbe;
        _searcher = searcher;
        _transcriber = transcriber;
        _refiner = refiner;
        _subs = subtitles;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _poll.Tick += OnPoll;
        _cueClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _cueClickTimer.Tick += (_, _) => { _cueClickTimer.Stop(); _ = TogglePlayPauseAsync(); }; // 單擊逾時未等到雙擊→播/暫停切換（#173）

        LoadBtn.Click += (_, _) => { if (_loading) _loadCts?.Cancel(); else _ = LoadFromInputAsync(); }; // 載入中兼作取消
        UrlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && !_loading) _ = LoadFromInputAsync(); };
        // 依關鍵字搜尋 YouTube（#171）：Search／Enter 搜尋；結果以表格呈現，點名稱載入、點連結開原頁、按需查網路字幕（#177）
        SearchBtn.Click += (_, _) => DoSearch();
        SearchBox.DropDownOpened += (_, _) => SearchBox.ItemsSource = _searchHistory.Load(); // #186：開下拉時填入關鍵字歷史
        SearchBox.PreviewKeyDown += OnSearchBoxKey;                                            // Enter 搜尋、Delete 刪選中歷史
        // 右側子頁籤（#177 版面重整）：搜尋下載 / 播放學習，以可見性切換（WebView2 不被卸載重建）
        SubTabSearch.Checked += (_, _) => ShowSubTab(showSearch: true);
        SubTabPlay.Checked += (_, _) => ShowSubTab(showSearch: false);
        ReplayBtn.Click += (_, _) => _ = ReplayCurrentAsync();
        ResumeBtn.Click += (_, _) => _ = ResumeAsync();
        NextBtn.Click += (_, _) => _ = SkipNextAsync();
        AddNoteBtn.Click += (_, _) => AddCurrent();
        // 字幕清單：單擊＝播/暫停切換（依此循環，#173）、雙擊＝跳到該句起點播放（#169）。以系統雙擊時間延後判定單擊，雙擊到達即取消單擊。
        CueList.PreviewMouseLeftButtonUp += (_, _) => { _cueClickTimer.Stop(); _cueClickTimer.Start(); };
        CueList.MouseDoubleClick += (_, _) => { _cueClickTimer.Stop(); _ = JumpToSelectedAsync(); };
        // 右鍵選單（#189）：Copy line＋快速指定/修正說話人（依游標下之列動態填入）
        CueList.ContextMenu = new System.Windows.Controls.ContextMenu();
        CueList.ContextMenuOpening += OnCueContextMenuOpening;
        // 說話人篩選＋來源疊加＋整檔 YAML 編修（epic #145 增量5／6）
        SpeakerFilter.SelectionChanged += (_, _) => ApplySpeakerFilter();
        // #189 Row2「🧠 AI」：Auto 基底→LLM 重分句＋標說話人（修斷句、不動時間）；Manual 基底→僅補說話人（斷句已好）
        InferSpeakersBtn.Click += (_, _) => { if (_isAuto) { RefineWithAi(); } else { InferSpeakers(_enricher, SpeakerSource.Dialogue); } };
        WebSpeakersBtn.Click += (_, _) => InferSpeakers(_webEnricher, SpeakerSource.Web); // 增量6b：網搜上網找（🌐 Script）
        EditYamlBtn.Click += (_, _) => EnterYamlEdit();
        TranscribeBtn.Click += (_, _) => Transcribe(); // #187：Whisper 抓聲音重轉字幕（跑前確認估算費用）
        PauseAtSpeaker.SelectionChanged += (_, _) => { if (!_populatingPauseAt) { ApplyPauseAtSpeaker(); } }; // 指定說話人才暫停（增量7）
        ApplyYamlBtn.Click += (_, _) => _ = ApplyYamlEditAsync();
        CancelYamlBtn.Click += (_, _) => CancelYamlEdit();
        Loaded += async (_, _) => await EnsureWebAsync();
        IsVisibleChanged += OnVisibleChanged; // 切走分頁：停輪詢＋暫停播放；切回：恢復輪詢

        // 影片清單（epic #145 增量4）＋依 theme 篩選（B）：點清單載入該片、篩選、初次載入
        VideoList.SelectionChanged += OnVideoSelect;
        ClearVideosBtn.Click += (_, _) => OnClearVideos(); // #165 清空影片清單
        VideoThemeFilter.SelectionChanged += (_, _) => { if (!_populatingVideoFilter) { RefreshVideoList(); } };
        VideoThemePicker.SelectionChanged += (_, _) => { if (!_populatingVideoPicker) { OnVideoThemePicked(); } }; // 內容區塊改指派所屬主題（#173）
        // 刪除改右鍵選單/Delete 鍵（#167，取代 Delete 按鈕）
        VideoList.ContextMenu = ListDeleteSupport.DeleteMenu(OnDeleteVideo);
        VideoList.PreviewMouseRightButtonDown += ListDeleteSupport.SelectItemUnderMouse;
        VideoList.KeyDown += (_, e) => { if (e.Key == Key.Delete) { OnDeleteVideo(); } };
        PopulateVideoThemeFilter();
        RefreshVideoList();
        PrefillSearchFromTheme(); // #171：以使用中主題關鍵字預填搜尋框
        ApplySubtitleDisplay();   // 字幕帶字級/粗體偏好（比照筆記，設定可調）
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
            PrefillSearchFromTheme();    // 反映主題關鍵字（#171）
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
            await File.WriteAllTextAsync(Path.Combine(dir, "player.html"), PlayerHtml());
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

    /// <summary>貼片列 Load／Enter：解析 UrlBox 之影片 ID → 載入並加入影片清單（epic #145 增量4）。</summary>
    private async Task LoadFromInputAsync()
    {
        var id = ExtractVideoId(UrlBox.Text);
        if (id is null) { SetStatus("Enter a valid YouTube link or 11-character video ID."); return; }
        await LoadVideoAsync(id, addToStore: true);
    }

    /// <summary>載入指定影片（抓字幕→導引播放）。<paramref name="addToStore"/>＝true 時加入影片清單（貼連結載入）；點清單載入者已在清單、不重加。</summary>
    private async Task LoadVideoAsync(string id, bool addToStore)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        SetLoading(true);
        _guiding = false; _poll.Stop();
        _lastPausedIndex = -1; _shownCue = -1; _playbackStarted = false;
        _currentTitle = null; // 增量6：新片重置標題（起播後自播放器重新取得）
        _currentVideoId = id;  // 字幕存檔鍵（#174）
        SetWatchUrl(id);       // #177：內容區塊顯示可點超連結網址
        SetVideoTitle(_videoStore.Load().Items.FirstOrDefault(i => i.VideoId == id)?.Title ?? id); // #187：先用清單標題/id，起播後以實名更新
        SubTabPlay.IsChecked = true; // #177：載入即切到「播放學習」子頁（WebView2 於此顯示、字幕在此學習）
        ClearCues();
        SubtitleBand.Inlines.Clear();
        SetControls(false);

        var cached = _subs.TryLoad(id); // #174：已存字幕優先（免重抓、保留說話人/YAML 編修）
        SetStatus(cached is not null ? "Loading saved subtitles…" : "Fetching subtitles…");

        try
        {
            IReadOnlyList<SubtitleCue> cues;
            if (cached is not null) // #174：用已存字幕
            {
                cues = cached.Cues;
                _isAuto = cached.IsAutoGenerated;
            }
            else
            {
                var result = await _fetcher.FetchAsync(id, ct); // 傳已解析 id（與播放器導向一致）＋可取消 token
                cues = result.Cues;
                _isAuto = result.IsAutoGenerated;
                _subs.Save(id, _isAuto, cues); // 首次抓取即存檔（#174）
            }
            SetCues(cues);
            UpdateSourceLabel(); // #189：顯示本片字幕來源（Auto／Manual，純顯示）
            await EnsureWebAsync();
            if (_webReady)
            {
                Web.CoreWebView2.Navigate($"https://{HostName}/player.html?v={id}");
                _guiding = true;
                _poll.Start();
                if (addToStore) // 貼連結載入＝加入影片清單（依 VideoId 去重、記錄使用中主題）；標題先用 id、起播後自播放器更新
                {
                    var a = ThemeStore.GetActive(_themes.Load());
                    var vi = _videoStore.Add(id, id, a?.Id, a?.Name, DateTimeOffset.Now);
                    _currentVideoItemId = vi.Id;
                    RefreshVideoList();
                }
                // #186：不自動播放。就緒訊息延到 OnPoll 確認播放器 ready 才顯（避免可嵌入被禁/無效影片時謊報）。
                SetStatus(_isAuto
                    ? $"{_cues.Count} auto-generated caption lines — loading player…"
                    : $"{_cues.Count} subtitle lines fetched — loading player…");
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

    // ---- Row1：初始資料來源（Auto／Manual 互斥切換，#189）----

    /// <summary>更新 Row1 字幕來源文字（#189）：**純顯示、不可操作**（避免誤會可切換）——Auto＝自動字幕、Manual＝人工字幕；未載入＝空。</summary>
    private void UpdateSourceLabel()
    {
        BaseSourceText.Text = _cues.Count == 0 ? "" : (_isAuto ? "Auto captions" : "Manual subtitles");
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
            ? "No videos yet. Paste a YouTube link above and Load."
            : "No videos for this theme."; // 有影片但本 theme 無
        VideoEmptyHint.Visibility = shown.Count > 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        UpdateVideoThemePicker(); // 與清單/標題/篩選同步：顯示目前影片之所屬主題（#173）
        RefreshLoadedFlags();     // #177：清單增/刪/載入後，同步搜尋結果各列 Load 鈕之「已加入」灰態
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

    // ---- 依關鍵字搜尋 YouTube（#171）：結果下拉選單、點選直接載入 ----

    /// <summary>搜尋框按鍵（#186）：Enter＝搜尋（收下拉）；下拉開啟時 Delete＝刪除目前選中之歷史筆並刷新下拉。</summary>
    private void OnSearchBoxKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DoSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && SearchBox.IsDropDownOpen && SearchBox.SelectedItem is string q && q.Length > 0)
        {
            _searchHistory.Delete(q);
            SearchBox.ItemsSource = _searchHistory.Load(); // 刷新下拉，反映刪除
            e.Handled = true;
        }
    }

    /// <summary>
    /// 以 SearchBox 關鍵字搜尋 YouTube（yt-dlp），套搜尋選項（上傳日期 sp 篩／長度 client 篩／最多筆數）；
    /// 於**進度提示視窗**內執行 yt-dlp 搜尋（顯進度、完成自動關、減少等待焦慮，#185），完成後填表；
    /// 表格之背景免費字幕探測與（若開）自動網搜於視窗關閉後續跑、以狀態列回報。新搜尋取代進行中者。
    /// </summary>
    private void DoSearch()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (q.Length == 0) { SetStatus("Type keywords to search YouTube (or paste a link and Load)."); return; }
        _searchHistory.Add(q);       // #186：記入關鍵字歷史（置頂去重）
        SearchBox.IsDropDownOpen = false;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource(); // 背景探測/自動網搜用（新搜尋取消）
        var dateToken = (DateFilter.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
        var lengthKey = (LengthFilter.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "";
        var max = MaxResultsValue();
        var fetch = string.IsNullOrEmpty(lengthKey) ? max : Math.Min(max * 3, 50); // 有長度篩→多抓再 client 篩、湊近 max
        AiActionWindow.RunAndShow(System.Windows.Window.GetWindow(this), "Searching YouTube",
            async (report, ct) =>
            {
                report($"Searching YouTube for “{q}”…");
                if (!string.IsNullOrEmpty(dateToken)) { report("• upload-date filter on"); }
                if (!string.IsNullOrEmpty(lengthKey)) { report($"• length filter: {lengthKey}"); }
                var results = await _searcher.SearchAsync(q, fetch, ct, string.IsNullOrEmpty(dateToken) ? null : dateToken);
                var filtered = VideoSearchFilter.ByLength(results, lengthKey, max);
                report(filtered.Count > 0 ? $"Found {filtered.Count} result(s) — loading table…" : "No results found.");
                PopulateResults(filtered); // 填表＋觸發背景免費字幕探測與（若開）自動網搜
                SetStatus(filtered.Count > 0
                    ? $"{filtered.Count} result(s) — click a row's Load button, or paste a link and Load."
                    : "No results. Try different keywords or relax the filters.");
                return null; // 搜尋本身免費、不顯費用
            },
            autoCloseOnSuccess: true, showCost: false);
    }

    /// <summary>最多筆數選項值（10/25/50；預設 25、界 1–50）。</summary>
    private int MaxResultsValue()
    {
        var s = (MaxResults.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string;
        return int.TryParse(s, out var n) ? Math.Clamp(n, 1, 50) : 25;
    }


    /// <summary>把搜尋結果填入可排序/過濾表格（#177）：即時顯示縮圖/名稱/連結/片長/推薦星等；內嵌字幕背景逐列免費探測填入；預設依推薦分排序。</summary>
    private void PopulateResults(IReadOnlyList<VideoSearchResult> results)
    {
        var firstSeen = _searchRecords.RecordAndGet(results.Select(r => r.VideoId).ToList(), DateTimeOffset.Now); // #186：記/取初次搜尋時間
        _searchRows = results.Select(r => new SearchRow(r.VideoId, r.Title, r.DurationSec,
            firstSeen.TryGetValue(r.VideoId, out var fs) ? fs : DateTimeOffset.Now)).ToList();
        RestoreCachedStatus(_searchRows);                                                // #188：還原已探測之字幕狀態（免重探內嵌、免重花額度查網路）
        RefreshLoadedFlags();                                                            // 依現有影片清單標示「已加入」（灰）
        _searchView = new System.Windows.Data.ListCollectionView(_searchRows);
        ApplyDefaultSort();                                                              // 預設：推薦分遞減、同分短片優先
        ApplyResultFilter();                                                             // 沿用目前過濾框文字
        SearchResultsGrid.ItemsSource = _searchView;
        if (SearchResultsGrid.Columns.Count > 0)
        {
            SearchResultsGrid.Columns[0].SortDirection = System.ComponentModel.ListSortDirection.Descending; // Rec 欄顯示排序箭頭
        }
        SearchResultsPanel.Visibility = _searchRows.Count > 0
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (_searchRows.Count > 0)
        {
            _ = ProbeEmbeddedForRowsAsync(_searchRows, _searchCts?.Token ?? CancellationToken.None); // 免費、背景、可被新搜尋取消
        }
    }

    /// <summary>依快取還原各列已探測之字幕狀態（#188）：內嵌（人工/自動）與網路（found/none＋來源）即時填入、免重探；未快取者維持 spinner 待背景探測。</summary>
    private void RestoreCachedStatus(IReadOnlyList<SearchRow> rows)
    {
        var map = _statusStore.Load();
        foreach (var row in rows)
        {
            if (!map.TryGetValue(row.VideoId, out var e)) { continue; }
            if (e.Manual.HasValue && e.Auto.HasValue) { row.SetEmbedded(e.Manual.Value, e.Auto.Value); } // 停 spinner、即時徽章
            if (e.Web == VideoSubtitleStatusStore.WebFound) { row.SetWebResult("✓", EmbBlue, "Web transcript: " + Truncate(e.WebSource ?? "", 60), found: true); }
            else if (e.Web == VideoSubtitleStatusStore.WebNone) { row.SetWebResult("✗", EmbGray, "No web transcript found", found: false); }
        }
    }

    /// <summary>逐列免費探測內嵌字幕（yt-dlp metadata、不下載）；限流併發、逐列完成即更新該列徽章；全部完成後重整檢視使推薦排序反映字幕結果。新搜尋（token 取消）即止。#188：只探測未從快取還原者，結果批次存檔（避免併發逐列寫檔互相覆蓋）。</summary>
    private async Task ProbeEmbeddedForRowsAsync(IReadOnlyList<SearchRow> rows, CancellationToken ct)
    {
        var pending = rows.Where(r => r.NeedsEmbeddedProbe).ToList();                    // #188：略過已快取還原者（免重探）
        using var gate = new SemaphoreSlim(4); // 限流：最多 4 個 yt-dlp 併發，免一次開 8 個行程
        var tasks = pending.Select(async row =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var info = await _fetcher.ProbeEmbeddedAsync(row.VideoId, ct);
                    row.SetEmbedded(info.HasManual, info.HasAuto);                       // Manual／Auto 各標 ✓／–（免費、免 AI）
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { /* 新搜尋取代→靜默 */ }
            catch (Exception) { row.SetEmbeddedUnknown(); }                              // 私人/移除/逾時→標「?」（不存檔、下次重探）
        }).ToList();
        try { await Task.WhenAll(tasks); } catch { /* 個別已處理 */ }
        if (!ct.IsCancellationRequested)
        {
            // 批次存內嵌結果（#188）：僅存探測成功者（ManualRank≠-1）；一次讀寫避免併發互相覆蓋。存檔後重搜同片免重探。
            _statusStore.SaveEmbeddedBatch(pending.Where(r => r.ManualRank != -1)
                .Select(r => (r.VideoId, r.ManualRank == 1, r.AutoRank == 1)));
            try { _searchView?.Refresh(); } catch { /* 重整盡力 */ } // 探測落定→依更新後推薦分重排（非即時、免逐列跳動）
        }
        // 預設開啟自動網搜（#184，USR 選「只查前幾筆」）：探測落定、推薦排名反映字幕後，對推薦分前 N 列自動網搜（花額度、背景、無模態）
        if (!ct.IsCancellationRequested && AutoWebCheck.IsChecked == true)
        {
            await AutoCheckWebTopAsync(rows, AutoWebCountValue(), ct);
        }
    }

    /// <summary>自動網搜筆數選項值（3/5/10；預設 5、界 1–20）。</summary>
    private int AutoWebCountValue()
    {
        var s = (AutoWebCount.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string;
        return int.TryParse(s, out var n) ? Math.Clamp(n, 1, 20) : 5;
    }

    /// <summary>
    /// 自動網搜推薦分前 <paramref name="topN"/> 列（#184）：只查尚未查者、逐一（不模態）、累計費用以台幣顯示於狀態列。
    /// 無金鑰則略過；新搜尋（ct 取消）即止；個別失敗還原該列供手動再試。
    /// </summary>
    private async Task AutoCheckWebTopAsync(IReadOnlyList<SearchRow> rows, int topN, CancellationToken ct)
    {
        var targets = rows.Where(r => r.WebRank == 0)                                   // 未查過者
                          .OrderByDescending(r => r.RecommendScore).ThenBy(r => r.DurationSec ?? int.MaxValue)
                          .Take(topN).ToList();
        if (targets.Count == 0) { return; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            SetStatus("Auto web-check skipped — OPENAI_API_KEY not set. Set it and restart to use web lookup.");
            return;
        }
        var themeForAi = ThemeStore.GetActive(_themes.Load())?.Name;
        double usd = 0; var done = 0;
        foreach (var row in targets)
        {
            if (ct.IsCancellationRequested) { return; }
            row.SetWebChecking();
            try
            {
                var result = await _webProbe.ProbeAsync(row.Title, null, ct, themeForAi);
                if (result.Found) { row.SetWebResult("✓", EmbBlue, "Web transcript: " + Truncate(result.Source, 60), found: true); }
                else { row.SetWebResult("✗", EmbGray, "No web transcript found", found: false); }
                _statusStore.SaveWeb(row.VideoId, result.Found, result.Source); // #188：存網路結果——重搜同片不再重花額度
                var itemUsd = result.Usages.Sum(u => AiCost.EstimateUsd(u.Model, u.InputTokens, u.OutputTokens, u.WebSearch) ?? 0);
                usd += itemUsd;
                _spendLedger.Record(itemUsd, DateTimeOffset.Now); // #189：花費記帳（逐列，取消時已花部分仍入帳）
                done++;
                SetStatus($"Auto web-check {done}/{targets.Count} — 約 NT${AiCost.ToTwd(usd):0.##} so far…");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { row.ResetWebButton(); } // 失敗→還原，可手動再查
        }
        SetStatus($"Auto web-check done — {done} checked，估算約 NT${AiCost.ToTwd(usd):0.##}（含網搜費，估算；匯率約 US$1≈NT${AiCost.UsdToTwd:0}）。");
    }

    /// <summary>預設排序（#177）：推薦分遞減、同分短片優先。使用者點 DataGrid 表頭排序時由 DataGrid 接手覆寫。</summary>
    private void ApplyDefaultSort()
    {
        if (_searchView is null) { return; }
        _searchView.SortDescriptions.Clear();
        _searchView.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            nameof(SearchRow.RecommendScore), System.ComponentModel.ListSortDirection.Descending));
        _searchView.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            nameof(SearchRow.DurationSec), System.ComponentModel.ListSortDirection.Ascending)); // 同分：短片優先（更好入門）
        _searchView.Refresh();
    }

    /// <summary>依現有影片清單標示各搜尋列之「已加入」狀態（#177）：清單內者停用 Load 鈕（灰、標 Added）。清單增刪時亦呼叫（見 RefreshVideoList）。</summary>
    private void RefreshLoadedFlags()
    {
        if (_searchRows.Count == 0) { return; }
        var added = new HashSet<string>(_videoStore.Load().Items.Select(i => i.VideoId), StringComparer.Ordinal);
        foreach (var row in _searchRows) { row.SetLoaded(added.Contains(row.VideoId)); }
    }

    /// <summary>過濾框變更→依標題（不分大小寫、含子字串）過濾表格列（#177）；空＝不過濾。</summary>
    private void OnResultFilterChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (FilterPlaceholder is null) { return; } // 防 InitializeComponent 期間佔位符尚未連結時之初始事件
        FilterPlaceholder.Visibility = string.IsNullOrEmpty(ResultFilterBox.Text)
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ApplyResultFilter();
    }

    private void ApplyResultFilter()
    {
        if (_searchView is null) { return; }
        var q = ResultFilterBox.Text?.Trim() ?? "";
        _searchView.Filter = q.Length == 0
            ? null
            : o => o is SearchRow r && r.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>表格列 Load 鈕→載入該影片到播放器（加入清單、記錄使用中主題）；載入後 RefreshVideoList→RefreshLoadedFlags 使該列轉「已加入」灰。</summary>
    private void OnSearchRowLoad(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not SearchRow row) { return; }
        UrlBox.Text = row.VideoId;
        _ = LoadVideoAsync(row.VideoId, addToStore: true);
    }

    /// <summary>表格點連結→於系統預設瀏覽器開 YouTube 原頁（沿用 AboutPage 開連結作法）。</summary>
    private void OnOpenExternalLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Could not open link: " + ex.Message); }
        e.Handled = true;
    }

    /// <summary>表格點「🌐 Check」→按需查該片是否有網路逐字稿（花 OpenAI 額度）；以對話視窗顯示進度與費用，結果回填該列。</summary>
    private void OnSearchRowWebProbe(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not SearchRow row) { return; }
        RunWebProbe(row);
    }

    /// <summary>查該片是否有網路逐字稿（花 OpenAI 額度、模態顯進度與費用）；結果回填該列並**存入快取**（#188：重搜同片免重花）。取消/失敗還原按鈕供再試。手動 Check 與右鍵重檢共用。</summary>
    private void RunWebProbe(SearchRow row)
    {
        row.SetWebChecking();
        var title = row.Title;
        var themeForAi = ThemeStore.GetActive(_themes.Load())?.Name; // 尚未載入指派主題→用使用中主題縮小網搜範圍
        AiActionWindow.RunAndShow(System.Windows.Window.GetWindow(this), "Check web subtitles",
            async (report, ct) =>
            {
                var progress = new System.Progress<string>(s => report(s));
                var result = await _webProbe.ProbeAsync(title, progress, ct, themeForAi);
                if (result.Found) { row.SetWebResult("✓", EmbBlue, "Web transcript: " + Truncate(result.Source, 60), found: true); }
                else { row.SetWebResult("✗", EmbGray, "No web transcript found", found: false); }
                _statusStore.SaveWeb(row.VideoId, result.Found, result.Source); // #188：存網路結果——重搜同片不再重花額度
                _spendLedger.Record(result.Usages.Sum(u => AiCost.EstimateUsd(u.Model, u.InputTokens, u.OutputTokens, u.WebSearch) ?? 0), DateTimeOffset.Now); // #189：花費記帳
                return result.Usages
                    .Select(u => new AiActionWindow.AiUsage(u.InputTokens, u.OutputTokens, u.Model, u.WebSearch))
                    .ToList();
            });
        if (row.WebResultVisibility != System.Windows.Visibility.Visible) { row.ResetWebButton(); } // 取消/失敗→還原按鈕供再試
    }

    /// <summary>右鍵表格列→選取該列（#188）：使右鍵選單「重新檢查」作用於游標下那一列（DataGrid 預設右鍵不選取）。</summary>
    private void OnResultRowRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as System.Windows.DependencyObject;
        while (dep is not null and not System.Windows.Controls.DataGridRow)
        {
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        }
        if (dep is System.Windows.Controls.DataGridRow gridRow) { SearchResultsGrid.SelectedItem = gridRow.Item; }
    }

    /// <summary>右鍵選單「重新檢查內嵌字幕（免費）」（#188）：清該列徽章、重顯 spinner、重探 yt-dlp、更新快取。</summary>
    private void OnRecheckEmbedded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (SearchResultsGrid.SelectedItem is not SearchRow row) { return; }
        row.SetEmbeddedChecking();
        _ = RecheckEmbeddedOneAsync(row, _searchCts?.Token ?? CancellationToken.None);
    }

    /// <summary>單列重探內嵌字幕並更新快取（#188 手動重檢）；失敗標「?」不存檔（下次可再試）。</summary>
    private async Task RecheckEmbeddedOneAsync(SearchRow row, CancellationToken ct)
    {
        try
        {
            var info = await _fetcher.ProbeEmbeddedAsync(row.VideoId, ct);
            row.SetEmbedded(info.HasManual, info.HasAuto);
            _statusStore.SaveEmbedded(row.VideoId, info.HasManual, info.HasAuto);
        }
        catch (OperationCanceledException) { /* 新搜尋取代→靜默 */ }
        catch (Exception) { row.SetEmbeddedUnknown(); }
        try { _searchView?.Refresh(); } catch { /* 盡力 */ }
    }

    /// <summary>右鍵選單「重新檢查網路字幕（花 OpenAI）」（#188）：走與手動 Check 相同流程（模態顯費用）並更新快取。</summary>
    private void OnRecheckWeb(object sender, System.Windows.RoutedEventArgs e)
    {
        if (SearchResultsGrid.SelectedItem is not SearchRow row) { return; }
        RunWebProbe(row);
    }

    /// <summary>以使用中主題之搜尋關鍵字預填搜尋框（#171）；使用者已輸入則不覆寫。</summary>
    private void PrefillSearchFromTheme()
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) { return; }
        var kw = ThemeStore.GetActive(_themes.Load())?.Keywords ?? "";
        if (!string.IsNullOrWhiteSpace(kw)) { SearchBox.Text = kw.Trim(); }
    }

    private void OnVideoSelect(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var it = (VideoList.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as VideoItem;
        if (it is null || it.Id == _currentVideoItemId) return; // 無選取或已是目前載入 → 不重載
        _currentVideoItemId = it.Id;
        UpdateVideoThemePicker(); // 反映所選影片之所屬主題（#173）
        UrlBox.Text = it.VideoId;
        _ = LoadVideoAsync(it.VideoId, addToStore: false); // 已在清單、不重加
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
        UrlBox.Text = "";
        WatchUrlRow.Visibility = System.Windows.Visibility.Collapsed; // #177：無載入→隱藏超連結網址
        VideoTitleText.Visibility = System.Windows.Visibility.Collapsed; // #187：無載入→隱藏標題
        UpdateVideoThemePicker(); // _currentVideoItemId null → 停用
        SetStatus("No video loaded. Search or paste a link to load one.");
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
var player,ready=false,lastErr=-1;
var vid=new URLSearchParams(location.search).get('v')||'';
var tag=document.createElement('script');tag.src="https://www.youtube.com/iframe_api";
document.head.appendChild(tag);
function onYouTubeIframeAPIReady(){player=new YT.Player('p',{height:'100%',width:'100%',videoId:vid,
 playerVars:{'playsinline':1,'rel':0,'modestbranding':1,'origin':location.origin},
 events:{'onReady':function(){ready=true;},
         'onError':function(e){lastErr=e.data;}}});}
window.li_time=function(){return (ready&&player&&player.getCurrentTime)?player.getCurrentTime():-1;};
window.li_title=function(){return (ready&&player&&player.getVideoData)?(player.getVideoData().title||''):'';};
window.li_err=function(){return lastErr;};
window.li_pause=function(){if(ready&&player)player.pauseVideo();};
window.li_play=function(){if(ready&&player)player.playVideo();};
window.li_toggle=function(){if(ready&&player){var s=(player.getPlayerState?player.getPlayerState():-1);if(s==1){player.pauseVideo();}else{player.playVideo();}}};
window.li_seek=function(t){if(ready&&player){player.seekTo(t,true);player.playVideo();}};
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
                _ = UpdateCurrentVideoTitleAsync(); // epic #145 增量4：就緒後自播放器取標題回寫影片清單
                ShowCue(0);                         // 顯示第一句＋啟用控制鈕（不自動播放）
                SetStatus(_isAuto
                    ? $"{_cues.Count} auto-generated lines — press ▶ Continue or double-click a line to play (pauses at each line)."
                    : $"{_cues.Count} lines loaded — press ▶ Continue or double-click a line to play (pauses at each line).");
            }

            var pause = PauseDecider.NextPause(t, _cues, _lastPausedIndex, pauseSpeaker: _pauseSpeaker, pauseNoSpeaker: _pauseNoSpeaker); // 指定說話人（或未標示者）才暫停（增量7／#189）
            if (pause >= 0)
            {
                _lastPausedIndex = pause;
                try { await Web.ExecuteScriptAsync("window.li_pause&&window.li_pause()"); } catch { /* 盡力暫停 */ }
                ShowCue(pause);
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
        if (!ReferenceEquals(CueList.SelectedItem, row))
        {
            _refreshingCues = true;      // 程式化選取，勿觸發 JumpToSelected 跳播
            CueList.SelectedItem = row;
            _refreshingCues = false;
        }
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
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        }
        return (dep as System.Windows.Controls.ListBoxItem)?.DataContext as CueRow;
    }

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
        if (_currentVideoId is not null) { _subs.Save(_currentVideoId, _isAuto, list); } // 存回（#174）
        if (_shownCue == i) { RenderClickable(_cues[i]); }                          // 當前句→重繪字幕帶（含新說話人前綴）
        PopulateSpeakerFilter();                                                    // 新說話人可能出現/消失→同步下拉
        PopulatePauseAtSpeaker();
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
        _lastPausedIndex = _shownCue - 1; // 允許重播後於本句結束再暫停
        await SeekAsync(_cues[_shownCue].StartSec);
    }

    private async Task ResumeAsync()
    {
        if (!_webReady) return;
        try { await Web.ExecuteScriptAsync("window.li_play&&window.li_play()"); } catch { /* 盡力續播 */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime(); // 系統雙擊判定時間（ms），供區分字幕清單單擊/雙擊（#173）

    /// <summary>字幕清單單擊：切換播放/暫停（依播放器實際狀態，依此循環，#173）；未就緒／無字幕／YAML 編修中則忽略。</summary>
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
        await SeekAsync(_cues[next].StartSec);
    }

    /// <summary>雙擊字幕句→跳到該句起點並播放（#169：改雙擊觸發，單擊僅選取）。</summary>
    private async Task JumpToSelectedAsync()
    {
        if (CueList.SelectedItem is not CueRow row || !_webReady) return;
        var i = row.Index;
        _lastPausedIndex = i - 1; // 允許雙擊當前句＝自其起點重播
        ShowCue(i);
        await SeekAsync(_cues[i].StartSec);
    }

    private async Task SeekAsync(double sec)
    {
        try { await Web.ExecuteScriptAsync($"window.li_seek&&window.li_seek({sec.ToString(CultureInfo.InvariantCulture)})"); }
        catch { /* 盡力跳播 */ }
    }

    // ---- 說話人字幕：CueRow 綁定、依說話人篩選、整檔 YAML 編修（epic #145 增量5，#154） ----

    /// <summary>設定目前字幕：建 CueRow（保留原始 index）、綁 CueList＋套說話人篩選檢視、重填說話人下拉、啟用工具列。</summary>
    private void SetCues(IReadOnlyList<SubtitleCue> cues)
    {
        _cues = cues;
        _rows = new List<CueRow>(_cues.Count);
        for (var i = 0; i < _cues.Count; i++) _rows.Add(new CueRow(i, _cues[i]));
        CueList.ItemsSource = _rows;
        _cueView = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);
        _speakerFilter = null; _filterNoSpeaker = false; // 新字幕一律不篩選（與下拉重置 All 一致）——修 YAML 套用後殘留舊篩選（此路徑未經 ClearCues 重置）
        _cueView.Filter = CueRowFilter;
        PopulateSpeakerFilter();
        PopulatePauseAtSpeaker(); // 指定說話人才暫停（增量7）
        var has = _cues.Count > 0;
        SpeakerFilter.IsEnabled = has;
        InferSpeakersBtn.IsEnabled = has;
        WebSpeakersBtn.IsEnabled = has;
        EditYamlBtn.IsEnabled = has;
        TranscribeBtn.IsEnabled = has; // #187：Whisper 重轉需有字幕載入
        ApplyScriptGate();             // #189：依登記之網路字幕狀態閘控 🌐 Script（已知無→停用）
    }

    /// <summary>清空字幕與清單、關工具列（載入新片／取消時）。編修中則先退出編修 UI。</summary>
    private void ClearCues()
    {
        if (_yamlEditing) ExitYamlEditUi();
        _cues = new List<SubtitleCue>();
        _rows = new List<CueRow>();
        CueList.ItemsSource = null;
        _cueView = null;
        _refreshingCues = true;
        SpeakerFilter.Items.Clear();
        _refreshingCues = false;
        _speakerFilter = null; _filterNoSpeaker = false;
        _pauseSpeaker = null;
        _pauseNoSpeaker = false; // #189
        _populatingPauseAt = true; PauseAtSpeaker.Items.Clear(); _populatingPauseAt = false;
        SpeakerFilter.IsEnabled = false;
        InferSpeakersBtn.IsEnabled = false;
        WebSpeakersBtn.IsEnabled = false;
        EditYamlBtn.IsEnabled = false;
        TranscribeBtn.IsEnabled = false; // #187
        BaseSourceText.Text = "";                                         // #189：清來源文字（載入完成後再顯）
        ResetRefineApplied();                                             // #189：新載入→管線重置（Row2/Row3 未套用）
        PauseAtSpeaker.IsEnabled = false;
    }

    /// <summary>重填說話人下拉：All＋各具名說話人（去重排序）；有具名又有未標示句時另加「(no speaker)」。預設選 All。</summary>
    private void PopulateSpeakerFilter()
    {
        _refreshingCues = true;
        SpeakerFilter.Items.Clear();
        SpeakerFilter.Items.Add(AllSpeakers);
        var names = _cues.Where(c => !string.IsNullOrEmpty(c.Speaker))
                         .Select(c => c.Speaker!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        foreach (var n in names) SpeakerFilter.Items.Add(n);
        if (names.Count > 0 && _cues.Any(c => string.IsNullOrEmpty(c.Speaker)))
            SpeakerFilter.Items.Add(NoSpeaker);
        _speakerFilter = null; _filterNoSpeaker = false;
        SpeakerFilter.SelectedIndex = 0; // All
        _refreshingCues = false;
    }

    /// <summary>下拉改變→更新篩選條件、重整檢視（僅影響顯示，不動 _cues／播放 index）。</summary>
    private void ApplySpeakerFilter()
    {
        if (_refreshingCues) return;
        var sel = SpeakerFilter.SelectedItem as string;
        _filterNoSpeaker = sel == NoSpeaker;
        _speakerFilter = (sel is null || sel == AllSpeakers || sel == NoSpeaker) ? null : sel;
        _refreshingCues = true;   // Refresh 濾掉當前選取項時會清選取觸發 SelectionChanged——抑制其誤觸跳播
        _cueView?.Refresh();
        _refreshingCues = false;
    }

    private bool CueRowFilter(object o)
    {
        if (o is not CueRow row) return true;
        if (_filterNoSpeaker) return string.IsNullOrEmpty(row.Cue.Speaker);
        if (_speakerFilter is null) return true;
        return string.Equals(row.Cue.Speaker, _speakerFilter, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 指定說話人才暫停（增量7）：Everyone＋各具名說話人；選定後導引播放只在該說話人之句到句暫停 ----

    /// <summary>重填 Pause-at 下拉：Everyone＋各具名說話人（去重排序）＋（有具名又有未標示句時）「(no speaker)」；保留選取（原選項已無則回 Everyone）；無具名說話人則停用。</summary>
    private void PopulatePauseAtSpeaker()
    {
        _populatingPauseAt = true;
        // 以目前選項文字保留（含 (no speaker)）；下拉尚未建時由狀態反推
        var prevSel = PauseAtSpeaker.SelectedItem as string ?? (_pauseNoSpeaker ? NoSpeaker : _pauseSpeaker) ?? EveryoneSpeaker;
        PauseAtSpeaker.Items.Clear();
        PauseAtSpeaker.Items.Add(EveryoneSpeaker);
        var names = _cues.Where(c => !string.IsNullOrEmpty(c.Speaker)).Select(c => c.Speaker!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var n in names) PauseAtSpeaker.Items.Add(n);
        // #189：有具名又有未標示句時，另加「只在未標示（unknown）之句暫停」選項
        if (names.Count > 0 && _cues.Any(c => string.IsNullOrEmpty(c.Speaker))) { PauseAtSpeaker.Items.Add(NoSpeaker); }
        var idx = PauseAtSpeaker.Items.IndexOf(prevSel);
        PauseAtSpeaker.SelectedIndex = idx >= 0 ? idx : 0;
        ApplyPauseAtSelection(PauseAtSpeaker.SelectedItem as string); // 依還原後之選項設定 _pauseSpeaker／_pauseNoSpeaker
        PauseAtSpeaker.IsEnabled = names.Count > 0; // 有具名說話人才有意義
        _populatingPauseAt = false;
    }

    /// <summary>下拉改變→設定暫停對象（Everyone＝全部；具名＝該說話人；(no speaker)＝只在未標示之句暫停）。</summary>
    private void ApplyPauseAtSpeaker() => ApplyPauseAtSelection(PauseAtSpeaker.SelectedItem as string);

    /// <summary>依 Pause-at 選項字串設定 _pauseSpeaker／_pauseNoSpeaker（#189）。</summary>
    private void ApplyPauseAtSelection(string? sel)
    {
        _pauseNoSpeaker = sel == NoSpeaker;
        _pauseSpeaker = (sel is null || sel == EveryoneSpeaker || sel == NoSpeaker) ? null : sel;
    }

    /// <summary>說話人來源：依台詞 AI 推斷（增量6）或 OpenAI 網搜上網找逐字稿（增量6b）。</summary>
    private enum SpeakerSource { Dialogue, Web }

    /// <summary>Row2 已套用之修正方法（#189）：無／🌐Script（網路逐字稿）／🧠AI（LLM 推理）。互斥——套一個即取代另一個。</summary>
    private enum Row2Method { None, Script, Analyze }

    /// <summary>依已套用狀態更新 Row2/Row3 按鈕外觀（#189）：已套用者前置 ✓ 表「保持按下」；並套用 🌐 Script 網路字幕狀態閘控。</summary>
    private void UpdateRefineButtons()
    {
        WebSpeakersBtn.Content = (_row2Applied == Row2Method.Script ? "✓ " : "") + ScriptLabel;
        InferSpeakersBtn.Content = (_row2Applied == Row2Method.Analyze ? "✓ " : "") + AnalyzeLabel;
        TranscribeBtn.Content = (_row3Applied ? "✓ " : "") + VoiceLabel;
        ApplyScriptGate();
    }

    /// <summary>
    /// 依已登記之網路字幕狀態閘控 🌐 Script 鈕（#189）：本片經探測／試跑確認**無網路逐字稿**（狀態＝none）→ 停用（免再花錢白試）；
    /// 有（found）或尚未確認（未登記）→ 維持啟用。只會在既有啟用態上「關」、不反向強制開（避免蓋掉載入中/AI 中之停用）。
    /// </summary>
    private void ApplyScriptGate()
    {
        if (_currentVideoId is null) { return; }
        if (_statusStore.Get(_currentVideoId)?.Web == VideoSubtitleStatusStore.WebNone)
        {
            WebSpeakersBtn.IsEnabled = false;
            WebSpeakersBtn.ToolTip = "No web transcript exists for this video (checked before) — Script is off. Re-check from the search page if needed.";
        }
        else
        {
            WebSpeakersBtn.ToolTip = "Use a web transcript to fix speakers (and sentence breaks when the base is Auto), keeping the timing. Uses your OpenAI key; confirms cost first.";
        }
    }

    /// <summary>重置 Row2/Row3 已套用狀態（#189）：換基底來源／載入新片時，管線自 Row1 重起。</summary>
    private void ResetRefineApplied()
    {
        _row2Applied = Row2Method.None;
        _row3Applied = false;
        UpdateRefineButtons();
    }

    /// <summary>
    /// 說話人疊加（epic #145 增量6／6b，#156／#145 §D）：以指定 <paramref name="enricher"/> 取每句說話人、非破壞併回
    /// （僅填補未標示、保留既有 ground truth），並存回字幕存檔（#174）。**會用到 API**——改以 <see cref="AiActionWindow"/>
    /// 模態對話視窗執行：顯示動作訊息與**估算 AI 費用**、完成按 OK 結束、期間可 Cancel。期間停用兩顆來源鈕與 Edit YAML；
    /// 模態阻擋主視窗故不會併發換片（仍留 stale guard 保險）。動作內 await 由對話視窗訊息迴圈續泵推進。
    /// </summary>
    private void InferSpeakers(ISpeakerEnricher enricher, SpeakerSource source)
    {
        if (_cues.Count == 0 || _yamlEditing || _inferring || _transcribing || _loading) { return; }
        var web = source == SpeakerSource.Web;
        // #189：跑前價錢評估＋本日/本小時累計花費（本 app 記帳）；使用者取消不花費
        if (!ConfirmAiRun(web ? "Web speaker lookup" : "AI speaker inference",
            web ? "上網找逐字稿、為每句標註說話人（OpenAI 網搜工具；依內容長度計費）。"
                : "由 AI 依台詞內容推測每句說話人（依內容長度計費）。",
            EstimateSpeakerInferenceUsd(web)))
        { return; }
        _inferring = true;
        InferSpeakersBtn.IsEnabled = false;
        WebSpeakersBtn.IsEnabled = false;
        EditYamlBtn.IsEnabled = false;
        TranscribeBtn.IsEnabled = false; // #187：推斷期間一併停用重轉
        var target = _cues;              // stale guard 基準
        var titleForAi = _currentTitle;
        var themeForAi = CurrentThemeName(); // #所屬主題：一併給 AI 輔助判斷角色/領域、縮小網搜範圍
        AiActionWindow.RunAndShow(System.Windows.Window.GetWindow(this),
            web ? "Web speaker lookup" : "AI speaker inference",
            async (report, ct) =>
            {
                var progress = new System.Progress<string>(s => report(s)); // enricher 逐步進度→對話視窗（減少等待焦慮）
                var result = await enricher.InferSpeakersAsync(target, titleForAi, progress, ct, themeForAi);
                if (!ReferenceEquals(_cues, target)) { report("Subtitle changed meanwhile — result discarded."); return null; }
                var merged = SpeakerInference.MergeSpeakers(target, result.Speakers);
                var filled = SpeakerInference.CountNewlyLabeled(target, merged);
                var keepShown = _shownCue;   // index 不變（僅補說話人）→ 保留當前句
                SetCues(merged);
                if (_currentVideoId is not null) { _subs.Save(_currentVideoId, _isAuto, merged); } // 存說話人結果（#174）
                _row2Applied = web ? Row2Method.Script : Row2Method.Analyze; // #189：標記 Row2 已套用（保持按下）
                if (web && _currentVideoId is not null)
                {
                    // #189：登記本片網路字幕有無——無逐字稿時 enricher 回全 null（見 OpenAiWebSpeakerEnricher）；有任一具名即視為 found。下次據此閘控 🌐 Script。
                    _statusStore.SaveWeb(_currentVideoId, result.Speakers.Any(s => !string.IsNullOrWhiteSpace(s)), null);
                }
                if (keepShown >= 0 && keepShown < _rows.Count) { ShowCue(keepShown); } // 重繪字幕帶（含新說話人前綴）
                report(filled > 0
                    ? $"Done — labeled {filled} more line(s) with a speaker."
                    : "Done — no new speaker labels could be added.");
                SetStatus(filled > 0
                    ? $"{(web ? "Web lookup" : "AI")} labeled {filled} more line(s) with a speaker."
                    : $"{(web ? "Web lookup" : "AI")}: no new speaker labels for this subtitle.");
                _spendLedger.Record(result.Usages.Sum(u => AiCost.EstimateUsd(u.Model, u.InputTokens, u.OutputTokens, u.WebSearch) ?? 0), DateTimeOffset.Now); // #189：實際花費記帳
                if (result.Usages.Count == 0) { return null; }
                return result.Usages
                    .Select(u => new AiActionWindow.AiUsage(u.InputTokens, u.OutputTokens, u.Model, u.WebSearch))
                    .ToList();
            });
        _inferring = false;
        var enable = _cues.Count > 0 && !_yamlEditing && !_loading;
        InferSpeakersBtn.IsEnabled = enable;
        WebSpeakersBtn.IsEnabled = enable;
        EditYamlBtn.IsEnabled = enable;
        TranscribeBtn.IsEnabled = enable; // #187
        UpdateSourceLabel();              // #189：恢復 Row1 狀態
        UpdateRefineButtons();            // #189：反映 Row2 已套用（✓）
    }

    private static IReadOnlyList<AiActionWindow.AiUsage> ToUsages(IReadOnlyList<SpeakerUsage> us) =>
        us.Select(u => new AiActionWindow.AiUsage(u.InputTokens, u.OutputTokens, u.Model, u.WebSearch)).ToList();

    /// <summary>
    /// Row2「🧠 AI」對 **Auto 基底**（#189）：LLM 把破碎自動字幕**重新併成完整句、修斷句並標說話人，時間沿用原格不變**
    /// （<see cref="SubtitleRefine.BuildCues"/>）。跑前確認費用＋記帳；成功取代字幕、存檔、標 Row2 已套用。Manual 基底走 <see cref="InferSpeakers"/>（僅補說話人）。
    /// </summary>
    private void RefineWithAi()
    {
        if (_cues.Count == 0 || _yamlEditing || _inferring || _transcribing || _loading) { return; }
        if (!ConfirmAiRun("AI analyze — re-segment",
            "由 AI 把破碎的自動字幕重新併成完整句、修正斷句並標說話人（時間不變；依內容長度計費）。",
            EstimateSpeakerInferenceUsd(false)))
        { return; }
        _inferring = true;
        InferSpeakersBtn.IsEnabled = false;
        WebSpeakersBtn.IsEnabled = false;
        EditYamlBtn.IsEnabled = false;
        TranscribeBtn.IsEnabled = false;
        var target = _cues;                 // stale guard 基準
        var titleForAi = _currentTitle;
        var themeForAi = CurrentThemeName();
        AiActionWindow.RunAndShow(System.Windows.Window.GetWindow(this), "AI analyze — re-segment",
            async (report, ct) =>
            {
                var progress = new System.Progress<string>(s => report(s));
                var result = await _refiner.RefineAsync(target, titleForAi, progress, ct, themeForAi);
                if (!ReferenceEquals(_cues, target)) { report("Subtitle changed meanwhile — result discarded."); return null; }
                var newCues = SubtitleRefine.BuildCues(target, result.Segments);
                _spendLedger.Record(result.Usages.Sum(u => AiCost.EstimateUsd(u.Model, u.InputTokens, u.OutputTokens, u.WebSearch) ?? 0), DateTimeOffset.Now); // #189：實際花費記帳
                if (newCues.Count == 0)
                {
                    report("AI returned no usable segments — kept the current subtitle.");
                    return result.Usages.Count == 0 ? null : ToUsages(result.Usages);
                }
                _lastPausedIndex = -1;          // 斷句換→到句暫停判定重起算（時間本身不變）
                SetCues(newCues);
                if (_currentVideoId is not null) { _subs.Save(_currentVideoId, _isAuto, newCues); } // 存重分句結果（#174）
                _row2Applied = Row2Method.Analyze; // #189：標記 Row2 已套用（保持按下）
                if (_rows.Count > 0) { ShowCue(0); }
                report($"Done — re-segmented into {newCues.Count} line(s); timing kept.");
                SetStatus($"AI re-segmented into {newCues.Count} line(s) — sentence breaks fixed, timing unchanged.");
                return result.Usages.Count == 0 ? null : ToUsages(result.Usages);
            });
        _inferring = false;
        var enable = _cues.Count > 0 && !_yamlEditing && !_loading;
        InferSpeakersBtn.IsEnabled = enable;
        WebSpeakersBtn.IsEnabled = enable;
        EditYamlBtn.IsEnabled = enable;
        TranscribeBtn.IsEnabled = enable;
        UpdateSourceLabel();
        UpdateRefineButtons();
    }

    /// <summary>
    /// Whisper 音訊轉錄（#187）：抓真實聲音重新轉錄字幕、修正 YouTube 自動字幕時間漂移，使到句暫停對齊實際發音。
    /// **會用金鑰＋下載音訊**——跑前先確認估算音訊時長、估算台幣費用與帳戶餘額說明（餘額無法以 API 金鑰讀取），
    /// 使用者取消則不花費；確認才於 <see cref="AiActionWindow"/> 執行。成功以轉錄結果取代字幕、存回字幕存檔（#174）並附**實際**費用。
    /// 期間停用四顆字幕來源鈕；模態阻擋主視窗故不併發換片（仍留 stale guard 保險）。
    /// </summary>
    private void Transcribe()
    {
        if (_cues.Count == 0 || _yamlEditing || _inferring || _transcribing || _loading || _currentVideoId is null) { return; }

        // 跑前確認（#187：跑前顯示餘額及估算台幣費用）：以目前字幕末句時間估音訊長度→估台幣；餘額無法以 API 金鑰讀取故明示改查 usage 頁。使用者取消不花費。
        var estSec = EstimateAudioSeconds();
        var estTwd = AiCost.ToTwd(AiCost.EstimateWhisperUsd(estSec));
        var confirm = System.Windows.MessageBox.Show(
            System.Windows.Window.GetWindow(this),
            "以 OpenAI Whisper 重新轉錄此影片的實際語音，修正字幕時間軸（使到句暫停更準）。\n\n" +
            $"估算音訊長度：約 {estSec / 60.0:0.#} 分鐘\n" +
            $"估算費用：約 NT${estTwd:0.##}（Whisper 每分鐘約 US${AiCost.WhisperUsdPerMinute:0.###}；匯率約 US$1≈NT${AiCost.UsdToTwd:0}）\n" +
            $"今日已花：約 NT${AiCost.ToTwd(_spendLedger.SpentToday(DateTimeOffset.Now)):0.##}　·　本小時：約 NT${AiCost.ToTwd(_spendLedger.SpentThisHour(DateTimeOffset.Now)):0.##}（本 app 記帳）\n\n" +
            "帳戶餘額無法以 API 金鑰自動讀取，請於 platform.openai.com/usage 查看。\n" +
            "實際費用依真實音訊長度計算，完成後顯示。\n\n" +
            "要開始轉錄嗎？",
            "Re-transcribe audio (Whisper)",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.OK) { return; }

        _transcribing = true;
        InferSpeakersBtn.IsEnabled = false;
        WebSpeakersBtn.IsEnabled = false;
        EditYamlBtn.IsEnabled = false;
        TranscribeBtn.IsEnabled = false;
        var target = _cues;                 // stale guard 基準
        var videoId = _currentVideoId;      // 以已解析 YouTube ID 抓音訊（與字幕擷取一致，yt-dlp 接受裸 id）
        AiActionWindow.RunAndShow(System.Windows.Window.GetWindow(this), "Re-transcribe audio (Whisper)",
            async (report, ct) =>
            {
                var progress = new System.Progress<string>(s => report(s)); // 下載／逐塊轉錄進度→對話視窗（減少等待焦慮）
                var result = await _transcriber.TranscribeAsync(videoId!, progress, ct);
                if (!ReferenceEquals(_cues, target)) { report("Subtitle changed meanwhile — result discarded."); return null; }
                _lastPausedIndex = -1;          // 時間軸全換→暫停判定自目前時間重新起算
                SetCues(result.Cues);
                _isAuto = true;                 // 機器轉錄（非人工字幕）
                if (_currentVideoId is not null) { _subs.Save(_currentVideoId, _isAuto, result.Cues); } // 存轉錄結果（#174）
                _row3Applied = true;            // #189：標記 Row3（Voice 精修時間）已套用
                if (_rows.Count > 0) { ShowCue(0); }
                var twd = AiCost.ToTwd(AiCost.EstimateWhisperUsd(result.AudioSeconds));
                _spendLedger.Record(AiCost.EstimateWhisperUsd(result.AudioSeconds), DateTimeOffset.Now); // #189：實際花費記帳
                report($"Done — {result.Cues.Count} line(s) transcribed from audio.");
                report($"實際 Whisper 費用 ≈ 約 NT${twd:0.##}（{result.AudioSeconds / 60.0:0.#} 分鐘音訊；估算，以 OpenAI 現價為準）");
                SetStatus($"Re-transcribed {result.Cues.Count} line(s) from audio — timing now follows the real speech.");
                return null; // 費用非 token 制，改由上方訊息行呈現實際台幣費用；不回 AiUsage
            },
            autoCloseOnSuccess: false, showCost: false); // 費用自算並以訊息呈現，故關閉視窗內建 token 費用列
        _transcribing = false;
        var enable = _cues.Count > 0 && !_yamlEditing && !_loading;
        InferSpeakersBtn.IsEnabled = enable;
        WebSpeakersBtn.IsEnabled = enable;
        EditYamlBtn.IsEnabled = enable;
        TranscribeBtn.IsEnabled = enable;
        UpdateSourceLabel(); // #189：恢復 Row1 狀態
        UpdateRefineButtons();            // #189：反映 Row3 已套用（✓）
    }

    /// <summary>估算目前影片音訊秒數（Whisper 跑前費用估算用）：以目前字幕末句開始時間為估（字幕大致涵蓋全片）；無字幕回 0。實際時長於轉錄時由 ffprobe 取得。</summary>
    private double EstimateAudioSeconds() => _cues.Count > 0 ? _cues[^1].StartSec : 0;

    /// <summary>AI 動作跑前確認（#189）：顯示本次估算＋本日/本小時累計花費（本 app 記帳、非帳戶餘額）；按 OK 才執行、取消回 false＝不花費。</summary>
    private bool ConfirmAiRun(string title, string whatDescription, double estUsd)
    {
        var now = DateTimeOffset.Now;
        var msg = whatDescription + "\n\n" +
            $"本次估算：約 NT${AiCost.ToTwd(estUsd):0.##}\n" +
            $"今日已花：約 NT${AiCost.ToTwd(_spendLedger.SpentToday(now)):0.##}　·　本小時：約 NT${AiCost.ToTwd(_spendLedger.SpentThisHour(now)):0.##}\n" +
            "（金額為本 app 記帳與估算，非 OpenAI 帳戶餘額；餘額請上 platform.openai.com/usage 查）\n\n" +
            "要執行嗎？";
        return System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this), msg, title,
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;
    }

    /// <summary>粗估說話人推斷/網搜費用（#189）：以字幕字數估 tokens × 代表性模型單價（web 另加網搜工具費）；僅供跑前概估，實際依回傳用量記帳。</summary>
    private double EstimateSpeakerInferenceUsd(bool web)
    {
        var chars = _cues.Sum(c => c.Text.Length);
        var inTok = chars / 4 + 500;          // 粗估：輸入 tokens ≈ 字元/4 ＋ prompt 額外
        var outTok = _cues.Count * 8 + 100;   // 粗估：輸出 speakers 陣列
        var model = web ? "gpt-4.1" : "gpt-4o-mini"; // 代表性模型（實際依設定；此為估算用）
        return AiCost.EstimateUsd(model, inTok, outTok, web) ?? 0;
    }

    /// <summary>進入整檔 YAML 編修：序列化目前字幕入編輯框、停導引＋暫停播放、切換清單→編輯面板。</summary>
    private void EnterYamlEdit()
    {
        if (_cues.Count == 0 || _yamlEditing) return;
        _guiding = false; _poll.Stop();
        if (_webReady) { try { _ = Web.ExecuteScriptAsync("window.li_pause&&window.li_pause()"); } catch { /* 盡力暫停 */ } }
        YamlBox.Text = SubtitleYaml.Serialize(_cues);
        _yamlEditing = true;
        CueList.Visibility = System.Windows.Visibility.Collapsed;
        YamlEditor.Visibility = System.Windows.Visibility.Visible;
        SpeakerFilter.IsEnabled = false;
        InferSpeakersBtn.IsEnabled = false;
        WebSpeakersBtn.IsEnabled = false;
        EditYamlBtn.IsEnabled = false;
        TranscribeBtn.IsEnabled = false; // #187：YAML 編修期間停用重轉
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
        if (_currentVideoId is not null) { _subs.Save(_currentVideoId, _isAuto, parsed); } // 存 YAML 編修結果（#174）

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
        SpeakerFilter.IsEnabled = has;
        InferSpeakersBtn.IsEnabled = has;
        WebSpeakersBtn.IsEnabled = has;
        EditYamlBtn.IsEnabled = has;
        TranscribeBtn.IsEnabled = has; // #187
        ApplyScriptGate();             // #189
        if (_webReady && IsVisible && has) { _guiding = true; _poll.Start(); }
    }

    private void ExitYamlEditUi()
    {
        _yamlEditing = false;
        YamlEditor.Visibility = System.Windows.Visibility.Collapsed;
        CueList.Visibility = System.Windows.Visibility.Visible;
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
        /// <summary>時間標（#189）：cue 起始位置「m:ss」（超過一小時「h:mm:ss」）＋兩空白，置於說話人之前、清單以淡色 Run 呈現。</summary>
        public string TimeLabel => FormatPos(Cue.StartSec) + "  ";
        /// <summary>說話人前綴（固定「名: 」;未知＝「unknown: 」,#189）——清單以粗體 Run 呈現。</summary>
        public string SpeakerLabel => SpeakerLabelOf(Cue.Speaker);
        /// <summary>台詞文字（清單以正常字重 Run 呈現）。</summary>
        public string Text => Cue.Text;
        /// <summary>就地更新說話人（#189 右鍵指定）：換 Cue 並通知 SpeakerLabel 變更，使清單該列即時更新、免重建（不跳捲軸）。</summary>
        public void UpdateSpeaker(string? speaker)
        {
            Cue = Cue with { Speaker = speaker };
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SpeakerLabel)));
        }
    }

    /// <summary>
    /// 搜尋結果表格一列 view-model（#177）：縮圖/名稱/連結建構即定；內嵌字幕（背景免費探測）與網路字幕（按需查）
    /// 狀態非同步更新，故 <see cref="System.ComponentModel.INotifyPropertyChanged"/>。網路字幕以三態呈現：未查＝按鈕、查中＝按鈕（「…」停用）、完成＝結果文字。
    /// </summary>
    private sealed class SearchRow : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void On([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public string VideoId { get; }
        public string Title { get; }
        public string WatchUrl { get; }
        public Uri WatchUri { get; }
        public System.Windows.Media.ImageSource? ThumbSource { get; }
        public int? DurationSec { get; }            // 排序鍵（片長，秒；未知＝null 排末）
        public string DurationText { get; }         // 顯示（m:ss／h:mm:ss／—）
        public string FirstSeen { get; }            // 初次搜尋時間（#186）：yyyy-MM-dd HH:mm，字串即可排序

        public SearchRow(string videoId, string title, int? durationSec, DateTimeOffset firstSeen)
        {
            VideoId = videoId;
            Title = title;
            WatchUrl = "https://www.youtube.com/watch?v=" + videoId;
            WatchUri = new Uri(WatchUrl);
            ThumbSource = MakeThumbSource(videoId);
            DurationSec = durationSec;
            DurationText = FormatDuration(durationSec);
            FirstSeen = firstSeen.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            RecomputeRecommend(null, null); // 初始（字幕未探測）＝暫定推薦分，探測完成後重算
        }

        // 載入鈕狀態（#177）：已在影片清單者停用並標「Added」（灰），否則「Load」可按
        private bool _isLoaded;
        public bool CanLoad => !_isLoaded;
        public string LoadText => _isLoaded ? "Added" : "Load";
        public void SetLoaded(bool loaded)
        {
            if (_isLoaded == loaded) { return; }
            _isLoaded = loaded;
            On(nameof(CanLoad));
            On(nameof(LoadText));
        }

        // 內嵌字幕兩態（yt-dlp 免費探測、背景非同步填入）：Manual(人工)、Auto(自動) 各一徽章；探測中顯旋轉動畫、完成後符號 ✓(有)／–(無)／?(不明)（#186）
        private string _manualText = "";
        public string ManualText { get => _manualText; private set { _manualText = value; On(); } }
        private System.Windows.Media.Brush _manualBrush = EmbGray;
        public System.Windows.Media.Brush ManualBrush { get => _manualBrush; private set { _manualBrush = value; On(); } }
        private string _autoText = "";
        public string AutoText { get => _autoText; private set { _autoText = value; On(); } }
        private System.Windows.Media.Brush _autoBrush = EmbGray;
        public System.Windows.Media.Brush AutoBrush { get => _autoBrush; private set { _autoBrush = value; On(); } }
        private System.Windows.Visibility _embeddedSpinnerVisibility = System.Windows.Visibility.Visible; // 探測中顯 spinner（#186）
        public System.Windows.Visibility EmbeddedSpinnerVisibility { get => _embeddedSpinnerVisibility; private set { _embeddedSpinnerVisibility = value; On(); } }
        // 排序鍵（數值，供 CollectionView 排序）：1 有／0 無／-1 未知或失敗
        private int _manualRank = -1;
        public int ManualRank { get => _manualRank; private set { _manualRank = value; On(); } }
        private int _autoRank = -1;
        public int AutoRank { get => _autoRank; private set { _autoRank = value; On(); } }
        /// <summary>探測完成：Manual／Auto 各依有無標 ✓（有色）／–（灰），停 spinner、更新排序鍵並重算推薦分。</summary>
        public void SetEmbedded(bool hasManual, bool hasAuto)
        {
            ManualText = hasManual ? "✓" : "–"; ManualBrush = hasManual ? EmbGreen : EmbGray; ManualRank = hasManual ? 1 : 0;
            AutoText = hasAuto ? "✓" : "–"; AutoBrush = hasAuto ? EmbAmber : EmbGray; AutoRank = hasAuto ? 1 : 0;
            EmbeddedSpinnerVisibility = System.Windows.Visibility.Collapsed;
            RecomputeRecommend(hasManual, hasAuto);
        }
        /// <summary>探測失敗（私人／移除／逾時）：Manual／Auto 皆標「?」（停 spinner、排序鍵 -1、推薦分視為未知）。</summary>
        public void SetEmbeddedUnknown()
        {
            ManualText = "?"; ManualBrush = EmbGray; ManualRank = -1;
            AutoText = "?"; AutoBrush = EmbGray; AutoRank = -1;
            EmbeddedSpinnerVisibility = System.Windows.Visibility.Collapsed;
            RecomputeRecommend(null, null);
        }
        /// <summary>是否仍需內嵌探測（#188）：spinner 仍轉＝未從快取還原、未探測過 → 由背景探測填入。</summary>
        public bool NeedsEmbeddedProbe => EmbeddedSpinnerVisibility == System.Windows.Visibility.Visible;
        /// <summary>切回「探測中」（#188 手動重檢）：清徽章、重顯 spinner、排序鍵歸未知，待重新探測填入。</summary>
        public void SetEmbeddedChecking()
        {
            ManualText = ""; ManualBrush = EmbGray; ManualRank = -1;
            AutoText = ""; AutoBrush = EmbGray; AutoRank = -1;
            EmbeddedSpinnerVisibility = System.Windows.Visibility.Visible;
            RecomputeRecommend(null, null);
        }

        // 網路字幕（按需查、花額度）：三態
        private string _webButtonText = "\U0001F310 Check";
        public string WebButtonText { get => _webButtonText; private set { _webButtonText = value; On(); } }
        private bool _webButtonEnabled = true;
        public bool WebButtonEnabled { get => _webButtonEnabled; private set { _webButtonEnabled = value; On(); } }
        private System.Windows.Visibility _webButtonVisibility = System.Windows.Visibility.Visible;
        public System.Windows.Visibility WebButtonVisibility { get => _webButtonVisibility; private set { _webButtonVisibility = value; On(); } }
        private string _webResultText = "";
        public string WebResultText { get => _webResultText; private set { _webResultText = value; On(); } }
        private System.Windows.Media.Brush _webResultBrush = EmbGray;
        public System.Windows.Media.Brush WebResultBrush { get => _webResultBrush; private set { _webResultBrush = value; On(); } }
        private System.Windows.Visibility _webResultVisibility = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility WebResultVisibility { get => _webResultVisibility; private set { _webResultVisibility = value; On(); } }
        private string _webResultTip = "";
        public string WebResultTip { get => _webResultTip; private set { _webResultTip = value; On(); } }
        private int _webRank; // 排序鍵：1 有／-1 無／0 未查
        public int WebRank { get => _webRank; private set { _webRank = value; On(); } }
        private System.Windows.Visibility _webCheckingVisibility = System.Windows.Visibility.Collapsed; // 查中顯 spinner（#186）
        public System.Windows.Visibility WebCheckingVisibility { get => _webCheckingVisibility; private set { _webCheckingVisibility = value; On(); } }

        /// <summary>切到「查中」：隱藏按鈕、顯旋轉動畫（#186）。</summary>
        public void SetWebChecking() { WebButtonVisibility = System.Windows.Visibility.Collapsed; WebCheckingVisibility = System.Windows.Visibility.Visible; }
        /// <summary>切到「完成」：隱藏按鈕/spinner、顯示結果符號（✓有／✗無），來源置 tooltip，更新排序鍵。</summary>
        public void SetWebResult(string text, System.Windows.Media.Brush brush, string tip, bool found)
        {
            WebButtonVisibility = System.Windows.Visibility.Collapsed;
            WebCheckingVisibility = System.Windows.Visibility.Collapsed;
            WebResultText = text; WebResultBrush = brush; WebResultTip = tip;
            WebResultVisibility = System.Windows.Visibility.Visible;
            WebRank = found ? 1 : -1;
        }
        /// <summary>還原按鈕（取消/失敗後供再試）：隱藏 spinner、顯示 Check 鈕。</summary>
        public void ResetWebButton() { WebButtonText = "\U0001F310 Check"; WebButtonEnabled = true; WebCheckingVisibility = System.Windows.Visibility.Collapsed; WebButtonVisibility = System.Windows.Visibility.Visible; }

        // ── 推薦優序（#177／#183，第一欄）：規則＝字幕品質（Manual>Auto>無）＋片長適學度；分數 1–5，以彩色圓標呈現（5＝最佳、綠；3＝琥珀；低＝灰）——比星等更好懂、仍可排序 ──
        private double _recommendScore;
        public double RecommendScore { get => _recommendScore; private set { _recommendScore = value; On(); } } // 排序鍵（預設遞減）
        private string _recommendLabel = "";
        public string RecommendLabel { get => _recommendLabel; private set { _recommendLabel = value; On(); } } // 圓標數字 1–5
        private System.Windows.Media.Brush _recommendBrush = EmbGray;
        public System.Windows.Media.Brush RecommendBrush { get => _recommendBrush; private set { _recommendBrush = value; On(); } } // 圓標底色（綠/琥珀/灰）
        public string RecommendTip =>
            "Recommended for learning — 5 = best. From subtitle quality (Manual > Auto) and length (a short single lesson beats a long compilation). Click this header to sort by it.";

        /// <summary>依字幕（<paramref name="hasManual"/>／<paramref name="hasAuto"/>，null＝未探測＝中性）與片長算推薦分（1–5）＋圓標色。</summary>
        private void RecomputeRecommend(bool? hasManual, bool? hasAuto)
        {
            int subScore = hasManual == true ? 3 : hasAuto == true ? 1 : hasManual is null ? 1 : 0; // 人工3／自動1／未知1／無0
            int total = Math.Clamp(subScore + DurationScore(DurationSec), 1, 5);
            RecommendScore = total;
            RecommendLabel = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RecommendBrush = total >= 4 ? EmbGreen : total == 3 ? EmbAmber : EmbGray; // 綠(4–5)／琥珀(3)／灰(1–2)
        }

        /// <summary>片長適學度加分：1–25 分＝2（理想單則）；&lt;1 分或 25–60 分＝1；&gt;60 分（合輯）＝0；未知＝1（中性）。</summary>
        private static int DurationScore(int? sec)
        {
            if (sec is null) return 1;
            if (sec < 60) return 1;
            if (sec <= 1500) return 2;
            if (sec <= 3600) return 1;
            return 0;
        }

        /// <summary>片長顯示：&lt;1 小時＝m:ss、≥1 小時＝h:mm:ss；未知／0＝—。</summary>
        private static string FormatDuration(int? sec)
        {
            if (sec is null || sec <= 0) return "—";
            var t = TimeSpan.FromSeconds(sec.Value);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }
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

    /// <summary>套用搜尋縮圖高度（#187，選項頁可調 28–120）：設 Thumb 尺寸 DP（寬＝高×16/9）、縮圖欄寬與 DataGrid 列高。</summary>
    public void ApplyThumbSize(double height)
    {
        var h = Math.Clamp(height, 28, 120);
        ThumbHeight = h;
        ThumbWidth = Math.Round(h * 16.0 / 9.0);
        SearchResultsGrid.RowHeight = h + 8;
        if (SearchResultsGrid.Columns.Count > 1) { SearchResultsGrid.Columns[1].Width = new System.Windows.Controls.DataGridLength(ThumbWidth + 10); } // 縮圖欄（index 1）
    }

    /// <summary>內容區塊網址之上顯示影片標題（#187）；空則隱藏。</summary>
    private void SetVideoTitle(string? title)
    {
        var t = (title ?? "").Trim();
        VideoTitleText.Text = t;
        VideoTitleText.Visibility = t.Length > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";

    // 搜尋結果表格三種字幕徽章色（#177）：Manual=綠、Auto=琥珀、Web=藍、無/不明=灰；凍結供背景探測續程更新安全。
    private static readonly System.Windows.Media.Brush EmbGreen = FrozenBrush(0x2E, 0x7D, 0x32);
    private static readonly System.Windows.Media.Brush EmbAmber = FrozenBrush(0xB0, 0x6A, 0x00);
    private static readonly System.Windows.Media.Brush EmbBlue = FrozenBrush(0x15, 0x65, 0xC0);
    private static readonly System.Windows.Media.Brush EmbGray = FrozenBrush(0x8A, 0x8A, 0x8A);

    private static System.Windows.Media.Brush FrozenBrush(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private void SetStatus(string msg) => StatusText.Text = msg;

    private void SetLoading(bool loading)
    {
        _loading = loading;
        UrlBox.IsEnabled = !loading;
        LoadBtn.Content = loading ? "Cancel" : "Load"; // LoadBtn 保持可按：載入中按下即取消抓字幕
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
