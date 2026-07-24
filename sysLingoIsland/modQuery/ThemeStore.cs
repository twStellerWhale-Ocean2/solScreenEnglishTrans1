using System.IO;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>一則命名多媒體主題（spec#9）：名稱＋描述文字＋可選圖片檔名＋是否使用中＋各色配色描述。跨媒體（截圖／影片／筆記）歸屬之單位。</summary>
public sealed class ThemeItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>搜尋關鍵字（#171）：供影片頁「依關鍵字查 YouTube」預填；舊 themes.json 無此鍵→反序列化為空。</summary>
    public string Keywords { get; set; } = "";
    /// <summary>自動屏蔽字串（#217）：逗號分隔原字串（半形 `,`／全形 `，` 皆可）——匯入字幕時自各句移除（如 `(SNORT)` 音效標記）；舊檔無此鍵→空＝不過濾。解析用 <see cref="ThemeStore.ParseBlockedWords"/>。</summary>
    public string BlockedWords { get; set; } = "";
    /// <summary>圖片檔名（<c>themes\{檔名}</c>）；null＝無圖。</summary>
    public string? Image { get; set; }
    public bool IsActive { get; set; }
    /// <summary>
    /// 【已由 <see cref="Colors"/> 取代，僅供相容遷移】各色配色描述（Issue #69）：色名 → 描述。舊 themes.json 之此鍵於
    /// <see cref="ThemeColors.Ensure"/> 一次性遷移入 <see cref="Colors"/> 後即不再讀寫。
    /// </summary>
    public Dictionary<string, string> ColorRules { get; set; } = new();

    /// <summary>
    /// 主題 12 色可編輯色盤（#189-checklist USR：六邊形 12 色、可點選改色、無名稱）：每槽＝色票 hex＋描述。
    /// 描述用途雙軌——影片頁「說話人字型色」（描述含說話人名即用該色）與 AI 記事自動配色（AI 依描述回符合之 hex→筆記底色，調淡）。
    /// 由 <see cref="ThemeColors.Ensure"/> 確保恆 12 槽（空則自 <see cref="ColorRules"/> 遷移、再補六邊形預設）。
    /// </summary>
    public List<ThemeColor> Colors { get; set; } = new();
}

/// <summary>主題色盤一槽（#189-checklist USR）：可編輯色票 hex＋套用規則描述。</summary>
public sealed class ThemeColor
{
    public string Hex { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>主題 12 色色盤預設與正規化（#189-checklist USR：六邊形 6 角＋6 邊共 12 高飽和色，供字型色/記事配色）。</summary>
public static class ThemeColors
{
    public const int Count = 12;

    /// <summary>六邊形 12 色預設（依色相環約 30° 一格、高飽和；白底可讀之字型色）。</summary>
    public static readonly string[] HexagonDefaults =
    {
        "#E53935", "#F4511E", "#FB8C00", "#FDD835", "#C0CA33", "#7CB342",
        "#43A047", "#00897B", "#00ACC1", "#1E88E5", "#5E35B1", "#D81B60",
    };

    /// <summary>確保 <paramref name="item"/>.Colors 恆 12 槽：空則自舊 <see cref="ThemeItem.ColorRules"/> 遷移（名→hex＋描述、保留使用者描述），再以六邊形預設補足；逾 12 截斷。純正規化、可單元測試。</summary>
    public static void Ensure(ThemeItem item)
    {
        if (item.Colors.Count == 0 && item.ColorRules.Count > 0)
        {
            foreach (var (name, hex) in NoteColors.Palette) // 依盤序遷移有描述之舊色
            {
                if (item.ColorRules.TryGetValue(name, out var d) && !string.IsNullOrWhiteSpace(d))
                {
                    item.Colors.Add(new ThemeColor { Hex = hex, Description = d.Trim() });
                }
            }
        }
        while (item.Colors.Count < Count)
        {
            item.Colors.Add(new ThemeColor { Hex = HexagonDefaults[item.Colors.Count], Description = "" });
        }
        if (item.Colors.Count > Count) { item.Colors.RemoveRange(Count, item.Colors.Count - Count); }
    }
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
            var d = JsonSerializer.Deserialize<ThemesData>(File.ReadAllText(_path)) ?? new ThemesData();
            foreach (var it in d.Items) { ThemeColors.Ensure(it); } // 每主題補齊 12 色槽（空則自舊 ColorRules 遷移）
            return d;
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

    /// <summary>更新主題搜尋關鍵字（#171）。</summary>
    public static void UpdateKeywords(ThemesData d, string id, string keywords)
    {
        var it = Find(d, id);
        if (it is not null)
        {
            it.Keywords = keywords?.Trim() ?? "";
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

    /// <summary>
    /// 解析自動屏蔽字串（#217，純函式）：以半形 <c>,</c> 或全形 <c>，</c> 分隔，逐項去空白、剔空、大小寫不敏感去重；
    /// null／空白回空清單（＝不過濾）。
    /// </summary>
    public static IReadOnlyList<string> ParseBlockedWords(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(w => w.Trim())
                 .Where(w => w.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();

    /// <summary>使用中主題之配色規則注入文字（Issue #69）；無使用中或全空回空字串（＝不啟用智能配色）。</summary>
    public string ActiveColorRules() => BuildColorRulesText(GetActive(Load()));

    /// <summary>
    /// 將某主題之各色描述組為查詢注入文字（Issue #69；#189-checklist 改 hex 為鍵）：略過空白描述，依槽序輸出
    /// 「<c>#hex＝「描述」</c>」以「；」相連；全空回空字串。純函式、可單元測試。供 <see cref="QueryService"/> 之 colorRules
    /// （AI 依此回符合之 <b>hex</b>（照抄）或空——取代原回色名，因 12 色可編輯、無固定名）。
    /// </summary>
    public static string BuildColorRulesText(ThemeItem? item)
    {
        if (item is null) { return ""; }
        ThemeColors.Ensure(item);
        var parts = new List<string>();
        foreach (var c in item.Colors)
        {
            if (!string.IsNullOrWhiteSpace(c.Description) && !string.IsNullOrWhiteSpace(c.Hex))
            {
                parts.Add($"{c.Hex.Trim()} = \"{c.Description.Trim()}\"");
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
