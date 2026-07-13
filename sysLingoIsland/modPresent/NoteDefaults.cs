using System.IO;
using System.Text.Json;

namespace LingoIsland.Present;

/// <summary>
/// 加入我的筆記之預設偏好（Issue #55）：持久化於 <c>%APPDATA%\LingoIsland\note-defaults.json</c>，
/// 供結果視窗「加入至哪個資料夾／套哪個底色」記憶，與智能配色規則（附加於查詢提示）。
/// <list type="bullet">
/// <item><see cref="FolderName"/>：空＝依使用中主題名為夾（無主題則預設夾）；非空＝固定資料夾名。</item>
/// <item><see cref="ColorHex"/>：加入時預設底色 hex（空＝無底色/白）；使用者於結果視窗改選即更新。</item>
/// </list>
/// 讀寫失敗退預設／靜默降級，不致命。（Issue #55 之全域 ColorRules 已於 #69 改為主題內各色描述，見 <see cref="LingoIsland.Query.ThemeItem.ColorRules"/>。）
/// </summary>
public static class NoteDefaults
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>空＝依使用中主題名為夾（無主題則預設夾）；非空＝固定資料夾名。</summary>
    public static string FolderName { get; set; } = "";

    /// <summary>加入時預設底色 hex（空＝無底色）。</summary>
    public static string ColorHex { get; set; } = "";

    private static string Path0 => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland", "note-defaults.json");

    // ColorRules 欄位仍保留於 DTO 以相容舊 note-defaults.json（讀時忽略；#69 起配色規則改存於主題）
    private sealed record Dto(string FolderName, string ColorHex, string? ColorRules = null);

    public static void Load()
    {
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(Path0));
            if (dto is not null)
            {
                FolderName = dto.FolderName ?? "";
                ColorHex = dto.ColorHex ?? "";
            }
        }
        catch { /* 缺檔／毀損：沿用預設值 */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path0)!);
            File.WriteAllText(Path0, JsonSerializer.Serialize(new Dto(FolderName, ColorHex), Opts));
        }
        catch { /* 寫入失敗不影響主流程 */ }
    }
}
