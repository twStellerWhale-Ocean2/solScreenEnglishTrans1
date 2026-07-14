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
    private readonly ThemeStore _themes;                           // 使用中主題（加入影片時記錄跨媒體歸屬）＋依 theme 篩選（B）
    private bool _populatingVideoFilter;                           // 重填篩選下拉期間抑制 SelectionChanged→重整
    private readonly ISpeakerEnricher _enricher;                   // 說話人來源疊加（epic #145 增量6：AI 推斷；會用到 API、按鈕觸發）
    private string? _currentVideoItemId;                           // 目前載入影片於清單之項 Id（供更新標題／選中）
    private readonly DispatcherTimer _poll;
    private IReadOnlyList<SubtitleCue> _cues = new List<SubtitleCue>();
    private int _lastPausedIndex = -1; // 上次已暫停之 cue（PauseDecider 用）
    private int _shownCue = -1;        // 目前字幕帶顯示之 cue
    private bool _webReady;
    private Task? _webInit;            // WebView2 單次初始化任務（避 Loaded 與 Load 併發重複 CreateAsync 擲例外）
    private bool _guiding;             // 導引播放中（輪詢到句暫停生效）
    private bool _polling;             // OnPoll 重入防護（async void 掛 100ms timer、橋接往返可能 >100ms）
    private bool _playbackStarted;     // 實際起播確認後才宣稱「逐句暫停」成功（避可嵌入被禁時謊報）
    private bool _isAuto;              // 目前字幕為自動生成（逐字滾動、較破碎）——供狀態提示
    private bool _loading;             // 抓字幕中（LoadBtn 兼作 Cancel）
    private CancellationTokenSource? _loadCts; // 抓字幕可取消（新 Load／取消鈕）
    private CancellationTokenSource? _inferCts; // AI 說話人推斷可取消（新 Load／新推斷取代，增量6）

    // 說話人字幕（epic #145 增量5）：CueList 綁 CueRow view-model（保留原始 _cues index，篩選/顯示不動播放 index）
    private List<CueRow> _rows = new();
    private System.ComponentModel.ICollectionView? _cueView; // _rows 之預設檢視，套說話人篩選
    private string? _speakerFilter;    // null＝全部；否則特定說話人名
    private bool _filterNoSpeaker;     // true＝僅顯示未標示說話人之句
    private bool _refreshingCues;      // 重建/篩選/程式化選取期間，抑制 SelectionChanged→跳播
    private bool _yamlEditing;         // 整檔 YAML 編修模式中
    private bool _inferring;           // AI 說話人推斷中（防重入、按鈕停用）（增量6）
    private string? _currentTitle;     // 目前影片標題（起播後取得，供 AI 推斷輔助判斷角色）（增量6）
    private string? _pauseSpeaker;     // 指定說話人才暫停（增量7）；null＝全部說話人皆暫停
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
                            ISpeakerEnricher enricher)
    {
        InitializeComponent();
        _fetcher = fetcher;
        _videoStore = videoStore;
        _themes = themes;
        _enricher = enricher;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _poll.Tick += OnPoll;

        LoadBtn.Click += (_, _) => { if (_loading) _loadCts?.Cancel(); else _ = LoadFromInputAsync(); }; // 載入中兼作取消
        UrlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && !_loading) _ = LoadFromInputAsync(); };
        ReplayBtn.Click += (_, _) => _ = ReplayCurrentAsync();
        ResumeBtn.Click += (_, _) => _ = ResumeAsync();
        NextBtn.Click += (_, _) => _ = SkipNextAsync();
        AddNoteBtn.Click += (_, _) => AddCurrent();
        CueList.SelectionChanged += (_, _) => _ = JumpToSelectedAsync();
        // 說話人篩選＋來源疊加＋整檔 YAML 編修（epic #145 增量5／6）
        SpeakerFilter.SelectionChanged += (_, _) => ApplySpeakerFilter();
        InferSpeakersBtn.Click += (_, _) => _ = InferSpeakersAsync();
        EditYamlBtn.Click += (_, _) => EnterYamlEdit();
        PauseAtSpeaker.SelectionChanged += (_, _) => { if (!_populatingPauseAt) { ApplyPauseAtSpeaker(); } }; // 指定說話人才暫停（增量7）
        ApplyYamlBtn.Click += (_, _) => _ = ApplyYamlEditAsync();
        CancelYamlBtn.Click += (_, _) => CancelYamlEdit();
        Loaded += async (_, _) => await EnsureWebAsync();
        IsVisibleChanged += OnVisibleChanged; // 切走分頁：停輪詢＋暫停播放；切回：恢復輪詢

        // 影片清單（epic #145 增量4）＋依 theme 篩選（B）：點清單載入該片、刪除、篩選、初次載入
        VideoList.SelectionChanged += OnVideoSelect;
        DeleteVideoBtn.Click += (_, _) => OnDeleteVideo();
        ClearVideosBtn.Click += (_, _) => OnClearVideos(); // #165 清空影片清單
        VideoThemeFilter.SelectionChanged += (_, _) => { if (!_populatingVideoFilter) { RefreshVideoList(); } };
        PopulateVideoThemeFilter();
        RefreshVideoList();
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
        _inferCts?.Cancel(); // 增量6：載入新片取消進行中的 AI 說話人推斷（免浪費 API、免過時結果跑到逾時）
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        SetLoading(true);
        SetStatus("Fetching subtitles…");
        _guiding = false; _poll.Stop();
        _lastPausedIndex = -1; _shownCue = -1; _playbackStarted = false;
        _currentTitle = null; // 增量6：新片重置標題（起播後自播放器重新取得）
        ClearCues();
        SubtitleBand.Inlines.Clear();
        SetControls(false);

        try
        {
            var result = await _fetcher.FetchAsync(id, ct); // 傳已解析 id（與播放器導向一致）＋可取消 token
            _isAuto = result.IsAutoGenerated;
            SetCues(result.Cues);
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
                // 「逐句暫停」成功訊息延到 OnPoll 確認實際起播才顯示——避免可嵌入被禁／無效影片時謊報成功。
                SetStatus(_isAuto
                    ? $"{_cues.Count} auto-generated caption lines (machine-transcribed) — starting playback…"
                    : $"{_cues.Count} subtitle lines fetched — starting playback…");
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
        DeleteVideoBtn.IsEnabled = VideoList.SelectedItem is not null;
    }

    /// <summary>以目前主題重填「依 theme 篩選」下拉（圖文）；期間抑制重整、保留選取。</summary>
    private void PopulateVideoThemeFilter()
    {
        _populatingVideoFilter = true;
        ThemeFilter.Populate(VideoThemeFilter, _themes);
        _populatingVideoFilter = false;
    }

    private System.Windows.Controls.StackPanel VideoItemView(VideoItem it)
    {
        var col = new System.Windows.Controls.StackPanel();
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
        return col;
    }

    private static string FormatTime(string iso) =>
        DateTimeOffset.TryParse(iso, out var t) ? t.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : iso;

    private void OnVideoSelect(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var it = (VideoList.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as VideoItem;
        DeleteVideoBtn.IsEnabled = it is not null;
        if (it is null || it.Id == _currentVideoItemId) return; // 無選取或已是目前載入 → 不重載
        _currentVideoItemId = it.Id;
        UrlBox.Text = it.VideoId;
        _ = LoadVideoAsync(it.VideoId, addToStore: false); // 已在清單、不重加
    }

    private void OnDeleteVideo()
    {
        var it = (VideoList.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as VideoItem;
        if (it is null) return;
        _videoStore.Remove(it.Id);
        if (it.Id == _currentVideoItemId) _currentVideoItemId = null;
        RefreshVideoList();
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
        _currentVideoItemId = null;
        RefreshVideoList();
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
 events:{'onReady':function(){ready=true;player.playVideo();},
         'onError':function(e){lastErr=e.data;}}});}
window.li_time=function(){return (ready&&player&&player.getCurrentTime)?player.getCurrentTime():-1;};
window.li_title=function(){return (ready&&player&&player.getVideoData)?(player.getVideoData().title||''):'';};
window.li_err=function(){return lastErr;};
window.li_pause=function(){if(ready&&player)player.pauseVideo();};
window.li_play=function(){if(ready&&player)player.playVideo();};
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

            if (!_playbackStarted) // 確認實際起播後才宣稱「逐句暫停」
            {
                _playbackStarted = true;
                _ = UpdateCurrentVideoTitleAsync(); // epic #145 增量4：起播後自播放器取標題回寫影片清單
                SetStatus(_isAuto
                    ? $"{_cues.Count} auto-generated caption lines (machine-transcribed) — playback pauses at each line; tap a word to look it up."
                    : $"{_cues.Count} subtitle lines loaded — playback pauses at each line; tap a word to look it up.");
            }

            var pause = PauseDecider.NextPause(t, _cues, _lastPausedIndex, pauseSpeaker: _pauseSpeaker); // 指定說話人才暫停（增量7）
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

    /// <summary>把字幕句以逐字可點呈現（說話人前置非可點；單字＝Hyperlink→WordLookupRequested；分隔＝純文字），沿用 EnglishWordTokenizer。</summary>
    private void RenderClickable(SubtitleCue cue)
    {
        SubtitleBand.Inlines.Clear();
        if (!string.IsNullOrEmpty(cue.Speaker)) // 說話人前置（粗體、非可點）——每句字幕前標示是誰在說（epic #145 增量5）
        {
            SubtitleBand.Inlines.Add(new Run(cue.Speaker + ": ")
            {
                FontWeight = System.Windows.FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x4A, 0x66)),
            });
        }
        foreach (var tok in EnglishWordTokenizer.Tokenize(cue.Text))
        {
            if (tok.IsWord)
            {
                var word = tok.Text;
                var link = new Hyperlink(new Run(word))
                {
                    Foreground = System.Windows.Media.Brushes.MediumVioletRed,
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

    private async Task SkipNextAsync()
    {
        if (_cues.Count == 0 || !_webReady) return;
        var next = _shownCue + 1;
        if (next >= _cues.Count) return;
        _lastPausedIndex = next - 1;
        ShowCue(next);
        await SeekAsync(_cues[next].StartSec);
    }

    private async Task JumpToSelectedAsync()
    {
        if (_refreshingCues) return; // 程式化選取／篩選重整，非使用者點選
        if (CueList.SelectedItem is not CueRow row || !_webReady) return;
        var i = row.Index;
        if (i == _shownCue) return; // 由 ShowCue 程式化選取觸發者早退，僅使用者手動點清單才跳播
        _lastPausedIndex = i - 1;
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
        EditYamlBtn.IsEnabled = has;
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
        _populatingPauseAt = true; PauseAtSpeaker.Items.Clear(); _populatingPauseAt = false;
        SpeakerFilter.IsEnabled = false;
        InferSpeakersBtn.IsEnabled = false;
        EditYamlBtn.IsEnabled = false;
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

    /// <summary>重填 Pause-at 下拉：Everyone＋各具名說話人（去重排序）；保留選取（該說話人已無則回 Everyone）；無具名說話人則停用。</summary>
    private void PopulatePauseAtSpeaker()
    {
        _populatingPauseAt = true;
        var prev = _pauseSpeaker;
        PauseAtSpeaker.Items.Clear();
        PauseAtSpeaker.Items.Add(EveryoneSpeaker);
        var names = _cues.Where(c => !string.IsNullOrEmpty(c.Speaker)).Select(c => c.Speaker!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var n in names) PauseAtSpeaker.Items.Add(n);
        var idx = prev is null ? 0 : PauseAtSpeaker.Items.IndexOf(prev);
        PauseAtSpeaker.SelectedIndex = idx >= 0 ? idx : 0;
        _pauseSpeaker = PauseAtSpeaker.SelectedIndex <= 0 ? null : prev;
        PauseAtSpeaker.IsEnabled = names.Count > 0; // 有具名說話人才有意義
        _populatingPauseAt = false;
    }

    /// <summary>下拉改變→設定「指定說話人才暫停」（Everyone＝null＝全部暫停）。</summary>
    private void ApplyPauseAtSpeaker()
    {
        var sel = PauseAtSpeaker.SelectedItem as string;
        _pauseSpeaker = (sel is null || sel == EveryoneSpeaker) ? null : sel;
    }

    /// <summary>
    /// AI 說話人疊加（epic #145 增量6，#156）：以 <see cref="ISpeakerEnricher"/> 依台詞逐句推斷說話人、非破壞併回
    /// （僅填補未標示、保留既有 ground truth）。**會用到 API、故按鈕觸發**。推斷期間停用按鈕、播放持續（僅補說話人、
    /// 文字/時間/播放 index 不變，保留當前句與到句暫停進度）；期間若載入新片／套用 YAML 使字幕換手則丟棄結果（stale guard）。
    /// </summary>
    private async Task InferSpeakersAsync()
    {
        if (_cues.Count == 0 || _yamlEditing || _inferring || _loading) return;
        _inferCts?.Cancel();
        _inferCts = new CancellationTokenSource();
        var ct = _inferCts.Token;
        _inferring = true;
        InferSpeakersBtn.IsEnabled = false;
        EditYamlBtn.IsEnabled = false;
        SetStatus("Inferring speakers from the dialogue with AI…");
        var target = _cues; // stale guard 基準
        try
        {
            var speakers = await _enricher.InferSpeakersAsync(target, _currentTitle, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_cues, target)) return; // 被取代／已換片／套用 YAML → 丟棄過時結果
            var merged = SpeakerInference.MergeSpeakers(target, speakers);
            var filled = SpeakerInference.CountNewlyLabeled(target, merged);
            var keepShown = _shownCue;       // index 不變（僅補說話人）→ 保留當前句
            SetCues(merged);
            if (keepShown >= 0 && keepShown < _rows.Count) ShowCue(keepShown); // 重繪字幕帶（含新說話人前綴）
            SetStatus(filled > 0
                ? $"AI labeled {filled} more line(s) with a speaker (inference from dialogue, not ground truth)."
                : "AI couldn't confidently add any new speaker labels for this subtitle.");
        }
        catch (SpeakerEnrichException ex) { SetStatus(ex.Message); }
        catch (OperationCanceledException) { /* 被新載入／新推斷取代 → 靜默，狀態由後續操作接手 */ }
        catch (Exception ex) { SetStatus("Speaker inference failed: " + ex.Message); }
        finally
        {
            _inferring = false;
            var enable = _cues.Count > 0 && !_yamlEditing && !_loading; // 載入中不啟用（避免併發載入時短暫可按但無效）
            InferSpeakersBtn.IsEnabled = enable;
            EditYamlBtn.IsEnabled = enable;
        }
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
        EditYamlBtn.IsEnabled = false;
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
        EditYamlBtn.IsEnabled = has;
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
    private sealed class CueRow
    {
        public CueRow(int index, SubtitleCue cue) { Index = index; Cue = cue; }
        public int Index { get; }
        public SubtitleCue Cue { get; }
        public string Display => string.IsNullOrEmpty(Cue.Speaker) ? Cue.Text : Cue.Speaker + ": " + Cue.Text;
    }

    private void SetStatus(string msg) => StatusText.Text = msg;

    private void SetLoading(bool loading)
    {
        _loading = loading;
        UrlBox.IsEnabled = !loading;
        LoadBtn.Content = loading ? "Cancel" : "Load"; // LoadBtn 保持可按：載入中按下即取消抓字幕
    }

    private void SetControls(bool enabled)
    {
        ReplayBtn.IsEnabled = enabled;
        ResumeBtn.IsEnabled = enabled;
        NextBtn.IsEnabled = enabled;
        AddNoteBtn.IsEnabled = enabled;
    }
}
