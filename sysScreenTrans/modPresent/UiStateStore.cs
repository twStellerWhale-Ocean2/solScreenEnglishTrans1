using System.IO;
using System.Text.Json;

namespace ScreenTrans.Present;

/// <summary>
/// 結果視窗的擺放狀態持久化（跨啟動記憶位置與大小）。
/// 存於 %APPDATA%\ScreenTrans\ui-state.json——使用者可寫、且不隨改建覆蓋
/// （有別於隨 exe 發佈的 appsettings.json）。讀寫失敗一律退回預設、不致命。
/// </summary>
public sealed class UiStateStore
{
    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }
    public double WinWidth { get; set; } = 560;
    public double WinHeight { get; set; } = 380;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTrans");

    private static string FilePath => Path.Combine(Dir, "ui-state.json");

    public static UiStateStore Load()
    {
        try
        {
            return JsonSerializer.Deserialize<UiStateStore>(File.ReadAllText(FilePath)) ?? new UiStateStore();
        }
        catch
        {
            return new UiStateStore(); // 缺檔或格式壞 → 預設
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 持久化失敗（權限等）不影響主流程
        }
    }
}
