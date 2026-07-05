using Velopack;
using Velopack.Sources;

namespace ScreenTrans;

/// <summary>檢查更新結果（關於分頁手動檢查回報用；自動檢查僅關心 Ready）。</summary>
public enum UpdateCheckResult
{
    /// <summary>新版已下載就緒。</summary>
    Ready,
    /// <summary>已是最新版本。</summary>
    UpToDate,
    /// <summary>檢查或下載失敗（離線、來源不可達）——不得誤報「已是最新」。</summary>
    Failed,
}

/// <summary>
/// Velopack 自動更新封裝（Issue #51）：背景檢查更新源→靜默下載→就緒後由 UI 顯示提示，
/// 「立即重啟更新」即時套用、否則結束時掛起套用（下次啟動即新版）。
/// 檢查/下載任何失敗（離線、來源不可達）一律靜默略過，不影響查詢主動線；
/// dev 裸跑（非 Velopack 安裝形態）整段跳過。UI 僅收事件、顯示字串歸 AppStatusText。
/// </summary>
public sealed class UpdateService
{
    /// <summary>預設更新源：本專案之 GitHub Releases（資產含 releases.win.json 與 nupkg）。</summary>
    public const string RepoUrl = "https://github.com/twStellerWhale-Ocean2/solScreenEnglishTrans1";

    /// <summary>更新源覆寫環境變數（測試縫／自訂佈署）：URL 或本地路徑 feed；不設＝GitHub Releases。</summary>
    public const string FeedOverrideEnv = "SCREENTRANS_UPDATE_URL";

    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    public UpdateService()
    {
        var feed = Environment.GetEnvironmentVariable(FeedOverrideEnv);
        _mgr = string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(RepoUrl, null, false))
            : new UpdateManager(feed);
    }

    /// <summary>是否為 Velopack 安裝形態；false（dev 裸跑／publish 夾直跑）時更新流程整段跳過。</summary>
    public bool IsSupported => _mgr.IsInstalled;

    /// <summary>已靜默下載就緒之新版版號（無＝null）。</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>新版下載就緒（參數＝版號字串）。於背景執行緒觸發，接收端自行切 Dispatcher。</summary>
    public event Action<string>? UpdateReady;

    /// <summary>
    /// 檢查更新源並靜默下載新版（已就緒者直接回 Ready、不重查）。
    /// 未安裝形態視為已是最新；失敗（離線等）回 Failed——手動檢查不得誤報「已是最新」。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAndDownloadAsync()
    {
        if (!IsSupported)
        {
            return UpdateCheckResult.UpToDate;
        }
        if (ReadyVersion is not null)
        {
            return UpdateCheckResult.Ready;
        }
        try
        {
            var info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                return UpdateCheckResult.UpToDate;
            }
            await _mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _pending = info;
            ReadyVersion = info.TargetFullRelease.Version.ToString();
            UpdateReady?.Invoke(ReadyVersion);
            return UpdateCheckResult.Ready;
        }
        catch
        {
            return UpdateCheckResult.Failed; // 離線／來源不可達／下載中斷：自動檢查靜默略過、手動檢查如實回報
        }
    }

    /// <summary>立即重啟套用已就緒之新版（關於頁「立即重啟更新」）；無就緒新版＝no-op。</summary>
    public void RestartToApply()
    {
        if (_pending is not null)
        {
            _mgr.ApplyUpdatesAndRestart(_pending);
        }
    }

    /// <summary>結束時掛起套用（不重啟；下次啟動即新版）；無就緒新版＝no-op、失敗不阻斷結束。</summary>
    public void ApplyOnExit()
    {
        if (_pending is null)
        {
            return;
        }
        try
        {
            _mgr.WaitExitThenApplyUpdates(_pending, silent: true, restart: false);
        }
        catch
        {
            // 套用排程失敗不阻斷程式結束；新版仍留在本機、下次檢查直接就緒
        }
    }
}
