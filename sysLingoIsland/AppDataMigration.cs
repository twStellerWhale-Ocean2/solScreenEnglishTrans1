using System.IO;

namespace LingoIsland;

/// <summary>
/// 品牌更名（solScreenEnglishTrans1 → solLingoIsland）之一次性資料遷移：
/// 舊版把設定/歷史/筆記/情境存於 <c>%APPDATA%\ScreenTrans</c>；更名後六個 store 皆改存
/// <c>%APPDATA%\LingoIsland</c>。首次啟動若新資料夾尚不存在而舊資料夾存在，將舊資料夾整棵
/// **複製**到新位置（刻意保留舊資料夾為備援——Velopack 回滾到舊版仍可讀其原資料夾）。
/// 任何失敗皆不致命：新版缺檔本就退預設、Save 會重建。
/// 註：新資料夾名 <c>LingoIsland</c> 與各 store 內硬編碼一致；若日後再更名，兩處需同步。
/// </summary>
public static class AppDataMigration
{
    private const string LegacyFolder = "ScreenTrans";
    private const string CurrentFolder = "LingoIsland";

    /// <summary>於進程極早期（Program.Main，App 建構前——store 為 App 之 field initializer）呼叫一次。</summary>
    public static void Run()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var legacy = Path.Combine(appData, LegacyFolder);
            var current = Path.Combine(appData, CurrentFolder);
            // 新資料夾已在＝已遷移或已是新版所建；舊資料夾不在＝無可遷移。兩者皆 no-op（含全新安裝）。
            if (Directory.Exists(current) || !Directory.Exists(legacy))
            {
                return;
            }
            CopyDirectory(legacy, current);
        }
        catch
        {
            // 遷移失敗不阻斷啟動：各 store 缺檔會退預設、Save 會重建
        }
    }

    /// <summary>遞迴複製資料夾（不覆寫既有檔；配合 Run 的「新夾不存在才遷」保證只在乾淨新夾落地）。</summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
    }
}
