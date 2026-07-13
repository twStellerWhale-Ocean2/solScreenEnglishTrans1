using Velopack;

namespace LingoIsland;

/// <summary>
/// 自訂進入點（Issue #51）：Velopack 之安裝/更新 hooks（--veloapp-* 引數）必須在進程一開始處理
/// （該類呼叫會自行結束進程），故不用 WPF 產生之 App.Main，csproj 以 StartupObject 指定本類。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        AppDataMigration.Run(); // 品牌更名一次性資料遷移（%APPDATA%\ScreenTrans → LingoIsland）；須在 App 建構前（store 為 field initializer）
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
