using System.IO;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>一則命名多媒體主題（spec#9）：名稱＋描述文字＋可選圖片檔名＋是否使用中＋各色配色描述。跨媒體（截圖／影片／筆記）歸屬之單位。</summary>
public sealed class ThemeItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>圖片檔名（<c>themes\{檔名}</c>）；null＝無圖。</summary>
    public string? Image { get; set; }
    public bool IsActive { get; set; }
    /// <summary>
    /// 各色配色描述（Issue #69）：色名（<see cref="NoteColors.Palette"/> 名）→ 描述；台詞符合某色描述即標該色、
    /// 都不符合＝白。取代 #55 之全域單一規則。舊 themes.json／contexts.json 無此鍵 → 反序列化為空、即無配色規則。
    /// </summary>
    public Dictionary<string, string> ColorRules { get; set; } = new();
}

/// <summary>我的主題根結構：命名主題清單。</summary>
public sealed class ThemesData
{
    public List<ThemeItem> Items { get; set; } = new();
}

/// <summary>
/// 多媒體主題本機儲存（[modQuery模組] 主題儲存契約，spec#9；取代 #14 單一 paramContextHint；情境升級為主題 #146）。
/// 存 <c>%APPDATA%\LingoIsland\themes.json</c>，圖片存 <c>%APPDATA%\LingoIsland\themes\</c>
/// （舊 <c>contexts.json</c>／<c>contexts\</c> 由 <see cref="LingoIsland.AppDataMigration"/> 一次性非破壞遷移）。
/// CRUD、單一「使用中」、以使用中之描述文字注入查詢皆為純函式、可單元測試；讀寫失敗退空／降級、金鑰不入主題。
/// 相容遷移：清單為空但舊 <c>paramContextHint</c> 非空 → 建一則「預設」主題並設為使用中。
/// </summary>
public sealed class ThemeStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _imageDir;

    public ThemeStore(string? path = null, string? imageDir = null)
    {
        _path = path ?? DefaultPath;
        _imageDir = imageDir ?? DefaultImageDir;
    }

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland");

    public static string DefaultPath => Path.Combine(DefaultDir, "themes.json");
    public static string DefaultImageDir => Path.Combine(DefaultDir, "themes");

    public ThemesData Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ThemesData>(File.ReadAllText(_path)) ?? new ThemesData();
        }
        catch
        {
            return new ThemesData();
        }
    }

    /// <summary>讀出並套用舊 paramContextHint 相容遷移（僅在清單為空時）；有遷移即存回。</summary>
    public ThemesData LoadMigrated(string? legacyHint)
    {
        var d = Load();
        if (Migrate(d, legacyHint))
        {
            Save(d);
        }
        return d;
    }

    public void Save(ThemesData d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(d, Opts));
        }
        catch { /* 寫入失敗不影響主流程 */ }
    }

    /// <summary>使用中主題之描述文字（查詢注入來源，spec#9）；無使用中則空字串（＝回歸現行行為）。</summary>
    public string ActiveText() => ActiveText(Load());

    // ---- 圖片 IO ----

    public string ImagePathFor(string fileName) => Path.Combine(_imageDir, fileName);

    /// <summary>寫入某主題圖片（png），回檔名（供設 <see cref="ThemeItem.Image"/>）。</summary>
    public string WriteImage(string id, byte[] png)
    {
        Directory.CreateDirectory(_imageDir);
        var name = id + ".png";
        File.WriteAllBytes(Path.Combine(_imageDir, name), png);
        return name;
    }

    public void DeleteImage(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) { return; }
        try { File.Delete(Path.Combine(_imageDir, fileName)); }
        catch { /* 不致命 */ }
    }

    // ---- 純函式（可單元測試，不觸檔案） ----

    /// <summary>新增主題之預設名稱（佔位符；#53 視為「名稱尚未填」、可被圖片自動辨識之作品名覆寫）。</summary>
    public const string DefaultName = "New Theme";

    /// <summary>
    /// 圖片自動解釋時是否可自動填入辨識到的作品名（#53）：目前名稱為空白或仍為預設佔位符 <see cref="DefaultName"/>
    /// ＝「尚未填」可填；使用者已鍵入實際名稱（非空白且非佔位）則不覆寫。純函式、可單元測試。
    /// </summary>
    public static bool ShouldAutoFillName(string? current) =>
        string.IsNullOrWhiteSpace(current) || current.Trim() == DefaultName;

    public static ThemeItem Add(ThemesData d, string name)
    {
        var item = new ThemeItem { Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim() };
        if (d.Items.Count == 0)
        {
            item.IsActive = true; // 首則預設使用中
        }
        d.Items.Add(item);
        return item;
    }

    public static void Rename(ThemesData d, string id, string name)
    {
        var it = Find(d, id);
        if (it is not null && !string.IsNullOrWhiteSpace(name))
        {
            it.Name = name.Trim();
        }
    }

    public static void UpdateText(ThemesData d, string id, string text)
    {
        var it = Find(d, id);
        if (it is not null)
        {
            it.Text = text ?? "";
        }
    }

    /// <summary>設定單一使用中（其餘取消）。</summary>
    public static void SetActive(ThemesData d, string id)
    {
        foreach (var it in d.Items)
        {
            it.IsActive = it.Id == id;
        }
    }

    public static ThemeItem? Find(ThemesData d, string id) => d.Items.FirstOrDefault(i => i.Id == id);

    public static ThemeItem? GetActive(ThemesData d) => d.Items.FirstOrDefault(i => i.IsActive);

    public static string ActiveText(ThemesData d) => GetActive(d)?.Text ?? "";

    /// <summary>使用中主題之配色規則注入文字（Issue #69）；無使用中或全空回空字串（＝不啟用智能配色）。</summary>
    public string ActiveColorRules() => BuildColorRulesText(GetActive(Load()));

    /// <summary>
    /// 將某主題之各色描述組為查詢注入文字（Issue #69）：略過空白描述，依盤序輸出「色名＝「描述」」以「；」相連；
    /// 全空回空字串。純函式、可單元測試。供 <see cref="QueryService"/> 之 colorRules（AI 依此回符合之色名或空）。
    /// </summary>
    public static string BuildColorRulesText(ThemeItem? item)
    {
        if (item is null || item.ColorRules.Count == 0)
        {
            return "";
        }
        var parts = new List<string>();
        foreach (var (name, _) in NoteColors.Palette) // 依盤序、僅取盤上有效色
        {
            if (item.ColorRules.TryGetValue(name, out var desc) && !string.IsNullOrWhiteSpace(desc))
            {
                parts.Add($"{name} = \"{desc.Trim()}\"");
            }
        }
        return string.Join("; ", parts);
    }

    /// <summary>移除一則；回傳被移除項（供呼叫端刪其圖片）。</summary>
    public static ThemeItem? Remove(ThemesData d, string id)
    {
        var it = Find(d, id);
        if (it is not null)
        {
            d.Items.Remove(it);
        }
        return it;
    }

    /// <summary>舊 paramContextHint 相容遷移：清單為空且 hint 非空 → 建「預設主題」設為使用中。回是否有遷移。</summary>
    public static bool Migrate(ThemesData d, string? legacyHint)
    {
        if (d.Items.Count == 0 && !string.IsNullOrWhiteSpace(legacyHint))
        {
            d.Items.Add(new ThemeItem { Name = "Default", Text = legacyHint.Trim(), IsActive = true });
            return true;
        }
        return false;
    }
}
