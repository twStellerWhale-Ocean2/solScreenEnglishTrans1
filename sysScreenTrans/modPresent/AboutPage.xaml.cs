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
        VersionText.Text = "版本 v" + (ver is null ? "?" : $"{ver.Major}.{ver.Minor}.{ver.Build}");

        _updates = updates;
        if (_updates is null || !_updates.IsSupported)
        {
            return; // 未安裝形態：更新區維持隱藏
        }
        UpdatePanel.Visibility = Visibility.Visible;
        CheckUpdateBtn.Click += async (_, _) => await CheckAsync();
        RestartUpdateBtn.Click += (_, _) => _updates.RestartToApply();
        _updates.UpdateReady += v => Dispatcher.BeginInvoke(() => ShowReady(v));
        if (_updates.ReadyVersion is not null)
        {
            ShowReady(_updates.ReadyVersion);
        }
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
            UpdateStatusText.Text = result == UpdateCheckResult.Failed
                ? AppStatusText.UpdateCheckFailed
                : AppStatusText.UpdateUpToDate;
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
