using System.IO;
using System.Reflection;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace ScreenTrans.Present;

/// <summary>
/// 關於分頁（Issue #34）：程式名／版本／版權／聯絡 email／logo。
/// Issue #51 增更新區：手動「檢查更新」與新版就緒後之「立即重啟更新」；
/// 僅 Velopack 安裝形態顯示（dev 裸跑隱藏）、狀態字串歸 <see cref="AppStatusText"/>。
/// </summary>
public partial class AboutPage : UserControl
{
    private readonly UpdateService? _updates;

    public AboutPage(UpdateService? updates = null)
    {
        InitializeComponent();
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "Version v" + (ver is null ? "?" : $"{ver.Major}.{ver.Minor}.{ver.Build}");
        ChangeLogBox.Text = LoadChangeLog(); // 更新紀錄（Issue #79）

        _updates = updates;
        if (_updates is null || !_updates.IsSupported)
        {
            return; // 未安裝形態：更新區維持隱藏
        }
        UpdatePanel.Visibility = Visibility.Visible;
        CheckUpdateBtn.Click += async (_, _) => await CheckAsync();
        RestartUpdateBtn.Click += (_, _) => _updates.RestartToApply();
        _updates.UpdateReady += v => Dispatcher.BeginInvoke(() => ShowReady(v));
        // #122：下載階段回饋（與「確認中」區分）——事件於背景執行緒觸發，切 Dispatcher 更新 UI
        _updates.DownloadStarted += () => Dispatcher.BeginInvoke(() => UpdateStatusText.Text = AppStatusText.UpdateDownloading);
        _updates.DownloadProgress += p => Dispatcher.BeginInvoke(() => UpdateStatusText.Text = AppStatusText.UpdateDownloadingPercent(p));
        if (_updates.ReadyVersion is not null)
        {
            ShowReady(_updates.ReadyVersion);
        }
    }

    /// <summary>讀嵌入之 CHANGELOG.md（Issue #79）供「更新紀錄」顯示；缺失/失敗回提示、不致命。</summary>
    private static string LoadChangeLog()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (s is null)
            {
                return "(No change log found)";
            }
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch
        {
            return "(Couldn't load change log)";
        }
    }

    /// <summary>以預設瀏覽器開啟外部連結（Issue #84 GitHub 連結）；失敗不致命。</summary>
    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* 開啟失敗不致命 */ }
        e.Handled = true;
    }

    /// <summary>手動檢查：檢查中鎖鈕；有新版轉就緒態、無新版顯示已是最新、失敗如實回報（不誤報最新）。</summary>
    private async Task CheckAsync()
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = AppStatusText.UpdateChecking;
        var result = await _updates!.CheckAndDownloadAsync();
        if (result == UpdateCheckResult.Ready && _updates.ReadyVersion is not null)
        {
            ShowReady(_updates.ReadyVersion);
        }
        else
        {
            // #122：已是最新 vs 各類失敗（離線/限流/暫時性/來源）各給對應訊息
            UpdateStatusText.Text = result == UpdateCheckResult.UpToDate
                ? AppStatusText.UpdateUpToDate
                : AppStatusText.UpdateFailureMessage(result);
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    /// <summary>新版就緒：顯示版號、換「立即重啟更新」鈕（檢查鈕收起）。</summary>
    private void ShowReady(string version)
    {
        UpdateStatusText.Text = AppStatusText.UpdateReady(version);
        CheckUpdateBtn.Visibility = Visibility.Collapsed;
        RestartUpdateBtn.Visibility = Visibility.Visible;
    }
}
