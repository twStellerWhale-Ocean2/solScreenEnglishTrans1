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
    private readonly Func<(string? Id, string? Name)> _activeTheme; // 加入影片時記錄使用中主題（跨媒體歸屬）
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
    private const string HostName = "lingoisland.player"; // WebView2 虛擬主機：以真實 https origin 供 player.html（避 YouTube Error 150/153 之 null/opaque-origin 內嵌拒絕）

    /// <summary>暫停句點選單字＝查該字（App 導向獨立字典視窗，沿用 spec#1 查詢）。</summary>
    public event Action<string>? WordLookupRequested;

    /// <summary>加入我的筆記（目前句原文；App 重譯後入既有 NotesStore）。</summary>
    public event Action<string>? AddToNotesRequested;

    public VideoCapturePage(ISubtitleFetcher fetcher, VideoStore videoStore, Func<(string? Id, string? Name)> activeTheme)
    {
        InitializeComponent();
        _fetcher = fetcher;
        _videoStore = videoStore;
        _activeTheme = activeTheme;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _poll.Tick += OnPoll;

        LoadBtn.Click += (_, _) => { if (_loading) _loadCts?.Cancel(); else _ = LoadFromInputAsync(); }; // 載入中兼作取消
        UrlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && !_loading) _ = LoadFromInputAsync(); };
        ReplayBtn.Click += (_, _) => _ = ReplayCurrentAsync();
        ResumeBtn.Click += (_, _) => _ = ResumeAsync();
        NextBtn.Click += (_, _) => _ = SkipNextAsync();
        AddNoteBtn.Click += (_, _) => AddCurrent();
        CueList.SelectionChanged += (_, _) => _ = JumpToSelectedAsync();
        Loaded += async (_, _) => await EnsureWebAsync();
        IsVisibleChanged += OnVisibleChanged; // 切走分頁：停輪詢＋暫停播放；切回：恢復輪詢

        // 影片清單（epic #145 增量4）：點清單載入該片、刪除、初次載入
        VideoList.SelectionChanged += OnVideoSelect;
        DeleteVideoBtn.Click += (_, _) => OnDeleteVideo();
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
        else if (_guiding && _webReady)
        {
            _poll.Start();
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
        SetStatus("Fetching subtitles…");
        _guiding = false; _poll.Stop();
        _lastPausedIndex = -1; _shownCue = -1; _playbackStarted = false;
        _cues = new List<SubtitleCue>();
        CueList.ItemsSource = null;
        SubtitleBand.Inlines.Clear();
        SetControls(false);

        try
        {
            var result = await _fetcher.FetchAsync(id, ct); // 傳已解析 id（與播放器導向一致）＋可取消 token
            _cues = result.Cues;
            _isAuto = result.IsAutoGenerated;
            CueList.ItemsSource = _cues;
            await EnsureWebAsync();
            if (_webReady)
            {
                Web.CoreWebView2.Navigate($"https://{HostName}/player.html?v={id}");
                _guiding = true;
                _poll.Start();
                if (addToStore) // 貼連結載入＝加入影片清單（依 VideoId 去重、記錄使用中主題）；標題先用 id、起播後自播放器更新
                {
                    var (tid, tname) = _activeTheme();
                    var vi = _videoStore.Add(id, id, tid, tname, DateTimeOffset.Now);
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
        VideoList.SelectionChanged -= OnVideoSelect;
        VideoList.Items.Clear();
        foreach (var it in d.Items)
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
        VideoEmptyHint.Visibility = d.Items.Count > 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        DeleteVideoBtn.IsEnabled = VideoList.SelectedItem is not null;
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

            var pause = PauseDecider.NextPause(t, _cues, _lastPausedIndex);
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
        if (i < 0 || i >= _cues.Count) return;
        _shownCue = i;
        SetControls(true); // 控制鈕於首次顯示字幕（自動暫停或點清單跳播）後才生效，避免暫停前空點無反應
        if (CueList.SelectedIndex != i) CueList.SelectedIndex = i; // 觸發 SelectionChanged→JumpToSelected，靠 _shownCue 早退
        CueList.ScrollIntoView(_cues[i]);
        RenderClickable(_cues[i].Text);
    }

    /// <summary>把字幕句以逐字可點呈現（單字＝Hyperlink→WordLookupRequested；分隔＝純文字），沿用 EnglishWordTokenizer。</summary>
    private void RenderClickable(string text)
    {
        SubtitleBand.Inlines.Clear();
        foreach (var tok in EnglishWordTokenizer.Tokenize(text))
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
        var i = CueList.SelectedIndex;
        if (i < 0 || i >= _cues.Count || !_webReady) return;
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
