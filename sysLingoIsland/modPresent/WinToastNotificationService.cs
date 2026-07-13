using CommunityToolkit.WinUI.Notifications;

namespace LingoIsland.Present;

/// <summary>
/// 原生 Windows 通知實作（[techItem桌面通知]，spec#10／#101）：以 CommunityToolkit `ToastNotificationManagerCompat`
/// 送出——未封裝桌面 App **首次 <c>Show</c> 時自動註冊 AUMID＋開始功能表捷徑**（安裝版沿用、dev 裸跑亦可發真通知），
/// 進通知中心可回看。任何失敗（無 AUMID／WinRT 不可用／註冊被拒）**降級為應用內浮層 <see cref="ToastNotifier"/>、不崩潰**。
/// 不實作點擊 activation（USR 不要點擊動作；compat 之 COM 註冊屬其內部基建、本服務不掛 <c>OnActivated</c>）。
/// </summary>
public sealed class WinToastNotificationService : INotificationService
{
    public void Show(string title, string body)
    {
        try
        {
            var builder = new ToastContentBuilder().AddText(title);
            foreach (var line in (body ?? string.Empty).Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    builder.AddText(line.Trim());
                }
            }
            builder.Show();
        }
        catch
        {
            // 未安裝／dev／WinRT 送出失敗 → 應用內浮層（右下角、逾時消失），確保回饋不遺失、不崩潰
            ToastNotifier.Show(string.IsNullOrWhiteSpace(body) ? title : title + "\n" + body);
        }
    }
}
