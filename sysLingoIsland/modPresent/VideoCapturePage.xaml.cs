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
/// 影片擷取分頁（[modVideoCapture模組]／[techApp桌面查詢工具] 擷取來源頁，spec#2）：【獲得】單一輸入框貼影片網址＋字幕檔網址（epic #178 增量6′「輸入 pivot」）→
/// 取字幕檔（<see cref="TranscriptFetch"/>）整理說話人＋台詞、Whisper 轉錄實際語音、<see cref="ITranscriptAligner"/> 逐句對齊建立字幕→
/// WebView2 內嵌 YouTube IFrame Player API 導引播放、<see cref="PauseDecider"/> 到句暫停顯字幕→暫停句逐字可點（<see cref="WordLookupRequested"/>，沿用既有查詢）→
/// 加入既有筆記（<see cref="AddToNotesRequested"/>）。與螢幕擷取並列之可插拔擷取來源、下游完全共用。
/// </summary>
public partial class VideoCapturePage : System.Windows.Controls.UserControl
{
    private readonly VideoStore _videoStore;                       // 影片清單持久化（epic #145 增量4）
    private readonly ThemeStore _themes;                           // 使用中主題（加入影片時記錄跨媒體歸屬）＋依 theme 篩選（B）＋內容區塊所屬主題指派（#173）
    private bool _populatingVideoFilter;                           // 重填篩選下拉期間抑制 SelectionChanged→重整
    private bool _populatingVideoPicker;                           // 重填「所屬主題」下拉期間抑制 SelectionChanged→重指派（#173）
    private readonly IAudioTranscriber _transcriber;               // Whisper 音訊轉錄（#187）：抓真實聲音重轉字幕、修時間漂移；會用 API＋下載音訊、按鈕觸發、跑前確認費用
    private readonly ITranscriptAligner _aligner;                   // 字幕主線 pivot（epic #178 增量5′）：字幕檔整理（說話人＋台詞）＋對齊 Whisper 聲音時間軸取時間；會用 API、載入首次觸發、跑前確認費用
    private readonly SubtitleStore _subs;                          // 字幕存檔：免重抓、保留說話人/YAML 編修（#174）
    private readonly VideoSubtitleStatusStore _statusStore = new();  // 字幕狀態快取（#188→增量6′）：獲得框存入字幕檔網址＋載入來源，載入未快取時取用建立字幕
    private readonly AiSpendLedger _spendLedger = new();             // AI 花費帳本（#189）：每次 AI 動作跑前顯示本日/本小時累計、事後記帳
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
    private string? _speakerFilter;    // null＝全部；否則特定說話人名
    private bool _filterNoSpeaker;     // true＝僅顯示未標示說話人之句
    private bool _refreshingCues;      // 重建/篩選/程式化選取期間，抑制 SelectionChanged→跳播
    private bool _yamlEditing;         // 整檔 YAML 編修模式中
    private bool _transcribing;        // Whisper 轉錄中（防重入、按鈕停用）（#187）
    private bool _row3Applied;         // #189：Row3（🎙Voice 精修時間）是否已套用
    private const string VoiceLabel = "\U0001F399 Voice";    // 🎙
    private string? _currentTitle;     // 目前影片標題（起播後取得，供 AI 推斷輔助判斷角色）（增量6）
    private string? _pauseSpeaker;     // 指定說話人才暫停（增量7）；null＝全部說話人皆暫停
    private bool _pauseNoSpeaker;      // #189：只在未標示（unknown）之句暫停（Pause-at 選 (no speaker)）
    private bool _populatingPauseAt;   // 重填 Pause-at 下拉期間抑制 SelectionChanged
    private const string AllSpeakers = "All speakers";
    private const string NoSpeaker = "(no speaker)";
    private const string EveryoneSpeaker = "Everyone"; // Pause-at 之「全部」選項（增量7）

    private const string HostName = "lingoisland.player"; // WebView2 虛擬主機：以真實 https origin 供 player.html（避 YouTube Error 150/153 之 null/opaque-origin 內嵌拒絕）
    private static readonly string PlayerCacheBust = Guid.NewGuid().ToString("N"); // 每次啟動唯一：WebView2 依 URL 快取 player.html，同名檔會餵**舊 JS**（改動/更新後不生效）——故 player.html 帶此唯一碼為檔名，保證載當前版本

    /// <summary>暫停句點選單字＝查該字（App 導向獨立字典視窗，沿用 spec#1 查詢）。</summary>
    public event Action<string>? WordLookupRequested;

    /// <summary>加入我的筆記（目前句原文；App 重譯後入既有 NotesStore）。</summary>
    public event Action<string>? AddToNotesRequested;

    public VideoCapturePage(VideoStore videoStore, ThemeStore themes,
                            SubtitleStore subtitles, IAudioTranscriber transcriber,
                            ITranscriptAligner aligner)
    {
        InitializeComponent();
        _videoStore = videoStore;
        _themes = themes;
        _transcriber = transcriber;
        _aligner = aligner;
        _subs = subtitles;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _poll.Tick += OnPoll;
        _cueClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _cueClickTimer.Tick += (_, _) => { _cueClickTimer.Stop(); if (_cueClickWasSelected) { _ = TogglePlayPauseAsync(); } }; // 單擊逾時未等到雙擊：已選中之句→播/暫停切換；未選中者僅選取（不動作）

        // 獲得（epic #178 增量6′「輸入 pivot」）：單一輸入框貼「影片網址＋字幕檔網址」→抽兩網址、走載入管線建立字幕（跑前確認費用）。多行框故 Ctrl+Enter 送出；空框顯範例佔位。
        AcqBuildBtn.Click += (_, _) => DoAcquireBuild();
        AcqInputBox.TextChanged += (_, _) => AcqPlaceholder.Visibility =
            string.IsNullOrEmpty(AcqInputBox.Text) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        AcqInputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) { DoAcquireBuild(); e.Handled = true; }
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
        // 說話人篩選＋來源疊加＋整檔 YAML 編修（epic #145 增量5／6）
        SpeakerFilter.SelectionChanged += (_, _) => ApplySpeakerFilter();
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
        string? transcriptUrl = null;
        if (cached is null)
        {
            // 未存＝首次載入：需以「字幕檔＋Whisper 對齊」建立（會花費）。先在**拆除目前畫面前**取字幕檔網址並確認費用——使用者取消則保留目前影片。
            transcriptUrl = _statusStore.Get(id)?.TranscriptUrl;
            if (string.IsNullOrWhiteSpace(transcriptUrl))
            {
                SetStatus("This video has no matched subtitle file yet — paste its subtitle-file URL above and Find to match it, then Load.");
                if (listItemId is not null) { RefreshVideoList(); } // 審查修：還原清單選取到實際目前影片（提前 return 不錯位到未載入之片）
                return;
            }
            if (!ConfirmBuildRun()) // 取消＝不花費、不拆除目前畫面
            {
                if (listItemId is not null) { RefreshVideoList(); } // 審查修：取消建立→還原清單選取
                return;
            }
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        // 審查修：**確認會實際載入後**才推進清單選取/主題選擇器（清單點選路徑）——否則上方提前 return 會使選取錯位、播放仍舊片。
        if (listItemId is not null) { _currentVideoItemId = listItemId; UpdateVideoThemePicker(); }

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

        try
        {
            IReadOnlyList<SubtitleCue> cues;
            if (cached is not null) // #174：用已存字幕（免費）
            {
                SetStatus("Loading saved subtitles…");
                cues = cached.Cues;
            }
            else // 首次：字幕檔＋Whisper 對齊管線（模態顯進度、費用）
            {
                cues = BuildCuesViaPipeline(id, transcriptUrl!, ct) ?? Array.Empty<SubtitleCue>();
                if (cues.Count == 0)
                {
                    SetStatus("Subtitles weren't built (canceled, too long, or failed) — Load again to retry."); // 審查修：中性訊息，取消/截斷/失敗共用（對話框已關、不指其看詳情）
                    return; // finally 仍 SetLoading(false)
                }
                _subs.Save(id, CurrentVideoTitle(), isAutoGenerated: false, cues); // 存建立結果（#174；增量5′ 已無 Auto/Manual 之分）
            }
            SetCues(cues);
            UpdateSourceLabel(); // 顯示本片字幕來源（增量5′：字幕檔＋Whisper 對齊，純顯示）
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
    /// 字幕主線建立管線（epic #178 增量5′，模態顯進度＋費用）：取字幕檔內容 → 去 HTML → AI 整理成逐句（說話人＋台詞）→ Whisper 轉錄影片音訊取時間軸 →
    /// AI 逐句對齊到聲音時間 → 組裝帶說話人＋時間之 cue（對不上者時間未知）。回建立之 cue；空/失敗/取消回 null（呼叫端據此中止載入）。費用：AI token 由視窗顯示、Whisper 依實際音長以訊息呈現，皆記帳。
    /// </summary>
    private IReadOnlyList<SubtitleCue>? BuildCuesViaPipeline(string id, string transcriptUrl, CancellationToken outerCt)
    {
        IReadOnlyList<SubtitleCue>? built = null;
        AiActionWindow.RunAndShow(System.Windows.Window.GetWindow(this), "Build subtitles (subtitle file + Whisper)",
            async (report, winCt) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, winCt);
                var ct = linked.Token;
                var progress = new System.Progress<string>(s => report(s));
                var usages = new List<AiActionWindow.AiUsage>();

                report("Reading the subtitle file…");
                var raw = await TranscriptFetch.FetchAsync(transcriptUrl, ct);
                var text = TranscriptAlign.StripToPlainText(raw);

                // 審查修：**逐段即時記帳**——每個付費階段一完成就記，任一後段失敗/取消仍記下已實際發生之花費（不使後續確認框累計低估）。
                var parsed = await _aligner.ParseTranscriptAsync(text, progress, ct);
                usages.AddRange(TranscriptCost(parsed.Usages));
                _spendLedger.Record(SumUsd(parsed.Usages), DateTimeOffset.Now);
                if (parsed.Truncated) // 審查修：字幕檔過長、AI 整理輸出被截斷——明確告知（非靜默回空、非誤指 URL）；已記 parse 費用
                {
                    report("This subtitle file is too long to organize in one pass — use a shorter transcript, then Load again.");
                    return usages; // built 留 null → 中止
                }
                if (parsed.Lines.Count == 0)
                {
                    report("No dialogue could be organized from that subtitle file — check the subtitle-file URL points to a real transcript.");
                    return usages;
                }

                report($"Transcribing audio for timing (Whisper)… ({parsed.Lines.Count} lines to align)");
                var asr = await _transcriber.TranscribeAsync(id, progress, ct);
                var whisperUsd = AiCost.EstimateWhisperUsd(asr.AudioSeconds);
                _spendLedger.Record(whisperUsd, DateTimeOffset.Now); // Whisper 成功即記（每分鐘制、依實際音長）

                var aligned = await _aligner.AlignAsync(parsed.Lines, asr.Cues, progress, ct);
                usages.AddRange(TranscriptCost(aligned.Usages));
                _spendLedger.Record(SumUsd(aligned.Usages), DateTimeOffset.Now);

                built = TranscriptAlign.Assemble(parsed.Lines, aligned.StartSecs);
                var alignedCount = built.Count(c => c.StartSec.HasValue);
                report($"Done — {built.Count} line(s) with speakers; {alignedCount} aligned to the audio timeline.");
                report($"實際 Whisper 費用 ≈ 約 NT${AiCost.ToTwd(whisperUsd):0.##}（{asr.AudioSeconds / 60.0:0.#} 分鐘音訊；估算，以 OpenAI 現價為準）");
                return usages; // AI token 費用由視窗呈現；Whisper 費用以上方訊息行呈現
            },
            autoCloseOnSuccess: false, showCost: true);
        return built;
    }

    /// <summary>字幕建立跑前確認（增量5′）：顯示流程與粗估費用＋本日/本小時累計（本 app 記帳）；按 OK 才建立、取消回 false＝不花費。實際音長未知，費用以範圍概估、完成後顯示實際。</summary>
    private bool ConfirmBuildRun()
    {
        var now = DateTimeOffset.Now;
        var msg =
            "為這支影片建立字幕：讀取字幕檔（含說話人）→ 以 Whisper 轉錄實際語音取得時間軸 → AI 逐句對齊。\n\n" +
            $"估算費用：約 NT$5 起（20–30 分鐘一集；Whisper 每分鐘約 US${AiCost.WhisperUsdPerMinute:0.###}＋AI 解析／對齊，影片越長越多）\n" +
            $"今日已花：約 NT${AiCost.ToTwd(_spendLedger.SpentToday(now)):0.##}　·　本小時：約 NT${AiCost.ToTwd(_spendLedger.SpentThisHour(now)):0.##}（本 app 記帳、非帳戶餘額）\n\n" +
            "帳戶餘額請於 platform.openai.com/usage 查看。完成後顯示實際費用。\n\n" +
            "要開始建立嗎？";
        return System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this), msg, "Build subtitles (subtitle file + Whisper)",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;
    }

    /// <summary>合計一組 AI 用量之估算 USD（未列價之模型以 0 計）。</summary>
    private static double SumUsd(IReadOnlyList<SpeakerUsage> usages)
        => usages.Sum(u => AiCost.EstimateUsd(u.Model, u.InputTokens, u.OutputTokens, u.WebSearch) ?? 0);

    // ---- Row1：初始資料來源（Auto／Manual 互斥切換，#189）----

    /// <summary>更新字幕來源文字（增量5′）：**純顯示**——來源固定＝字幕檔（含說話人）＋Whisper 聲音對齊（已無 Auto/Manual 之分）；未載入＝空。</summary>
    private void UpdateSourceLabel()
    {
        BaseSourceText.Text = _cues.Count == 0 ? "" : "Subtitle file + Whisper timing";
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

    /// <summary>把 <see cref="SpeakerUsage"/> 轉為對話視窗費用顯示用之 <see cref="AiActionWindow.AiUsage"/> 清單。</summary>
    private static List<AiActionWindow.AiUsage> TranscriptCost(IReadOnlyList<SpeakerUsage> usages)
        => usages.Select(u => new AiActionWindow.AiUsage(u.InputTokens, u.OutputTokens, u.Model, u.WebSearch)).ToList();

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
        // 否則未建片若於 ConfirmBuildRun 取消／無字幕檔而提前 return，選取會錯位到新片、播放仍舊片。
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
                _ = UpdateCurrentVideoTitleAsync(); // epic #145 增量4：就緒後自播放器取標題回寫影片清單
                ShowCue(0);                         // 顯示第一句＋啟用控制鈕（不自動播放）
                SetStatus($"{_cues.Count} lines loaded — press ▶ Continue to play (pauses at each line), or double-click a line to jump there (paused).");
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
        EditYamlBtn.IsEnabled = has;
        SyncTranscribeEnabled(has); // #187 有字幕載入；增量5′：已帶說話人則停用（見方法）
    }

    /// <summary>同步 🎙Voice（Whisper 重轉）啟用（增量5′ 審查修）：字幕**已帶說話人**時停用——純 Whisper 重轉無說話人、會覆蓋抹除說話人（pivot 差異化），不容此 footgun；僅無說話人之字幕可重轉取時間。附說明 ToolTip。</summary>
    private void SyncTranscribeEnabled(bool baseEnabled)
    {
        var hasSpeaker = _cues.Any(c => !string.IsNullOrEmpty(c.Speaker));
        TranscribeBtn.IsEnabled = baseEnabled && !hasSpeaker;
        TranscribeBtn.ToolTip = hasSpeaker
            ? "Disabled: this subtitle already carries speakers from the subtitle file — re-transcribing with Whisper would drop them."
            : null;
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

    /// <summary>依已套用狀態更新 Row3（🎙 Voice）按鈕外觀（#189）：已套用者前置 ✓ 表「保持按下」。</summary>
    private void UpdateRefineButtons()
    {
        TranscribeBtn.Content = (_row3Applied ? "✓ " : "") + VoiceLabel;
    }

    /// <summary>重置 Row3 已套用狀態（#189）：換基底來源／載入新片時，管線自 Row1 重起。</summary>
    private void ResetRefineApplied()
    {
        _row3Applied = false;
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
        if (_cues.Count == 0 || _yamlEditing || _transcribing || _loading || _currentVideoId is null) { return; }

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
                if (_currentVideoId is not null) { _subs.Save(_currentVideoId, CurrentVideoTitle(), isAutoGenerated: false, result.Cues); } // 存轉錄結果（#174；增量5′ 已無 Auto/Manual 之分）
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
        EditYamlBtn.IsEnabled = enable;
        SyncTranscribeEnabled(enable); // 增量5′：已帶說話人則停用
        UpdateSourceLabel(); // #189：恢復 Row1 狀態
        UpdateRefineButtons();            // #189：反映 Row3 已套用（✓）
    }

    /// <summary>估算目前影片音訊秒數（Whisper 跑前費用估算用）：以目前字幕**最後一個有值**之開始時間為估（字幕大致涵蓋全片）；無字幕／皆未定時回 0。#184：容忍未定時句。實際時長於轉錄時由 ffprobe 取得。</summary>
    private double EstimateAudioSeconds() => _cues.LastOrDefault(c => c.StartSec.HasValue)?.StartSec ?? 0;

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
        SpeakerFilter.IsEnabled = has;
        EditYamlBtn.IsEnabled = has;
        SyncTranscribeEnabled(has); // #187；增量5′：已帶說話人則停用
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
        /// <summary>時間標（#189）：cue 起始位置「m:ss」（超過一小時「h:mm:ss」）＋兩空白，置於說話人之前、清單以淡色 Run 呈現。#184：未定時句（null）→ 無時間標（空字串）。</summary>
        public string TimeLabel => Cue.StartSec is double s ? FormatPos(s) + "  " : "";
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
