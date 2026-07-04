using System.IO;
using System.Text.Json;

namespace ScreenTrans.Query;

/// <summary>一則命名應用情境（spec#9）：名稱＋描述文字＋可選圖片檔名＋是否使用中。</summary>
public sealed class ContextItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>圖片檔名（<c>contexts\{檔名}</c>）；null＝無圖。</summary>
    public string? Image { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>我的情境根結構：命名情境清單。</summary>
public sealed class ContextsData
{
    public List<ContextItem> Items { get; set; } = new();
}

/// <summary>
/// 應用情境本機儲存（[modQuery模組] 情境儲存契約，spec#9；取代 #14 單一 paramContextHint）。
/// 存 <c>%APPDATA%\ScreenTrans\contexts.json</c>，圖片存 <c>%APPDATA%\ScreenTrans\contexts\</c>。
/// CRUD、單一「使用中」、以使用中之描述文字注入查詢皆為純函式、可單元測試；讀寫失敗退空／降級、金鑰不入情境。
/// 相容遷移：清單為空但舊 <c>paramContextHint</c> 非空 → 建一則「預設」情境並設為使用中。
/// </summary>
public sealed class ContextStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _imageDir;

    public ContextStore(string? path = null, string? imageDir = null)
    {
        _path = path ?? DefaultPath;
        _imageDir = imageDir ?? DefaultImageDir;
    }

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTrans");

    public static string DefaultPath => Path.Combine(DefaultDir, "contexts.json");
    public static string DefaultImageDir => Path.Combine(DefaultDir, "contexts");

    public ContextsData Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ContextsData>(File.ReadAllText(_path)) ?? new ContextsData();
        }
        catch
        {
            return new ContextsData();
        }
    }

    /// <summary>讀出並套用舊 paramContextHint 相容遷移（僅在清單為空時）；有遷移即存回。</summary>
    public ContextsData LoadMigrated(string? legacyHint)
    {
        var d = Load();
        if (Migrate(d, legacyHint))
        {
            Save(d);
        }
        return d;
    }

    public void Save(ContextsData d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(d, Opts));
        }
        catch { /* 寫入失敗不影響主流程 */ }
    }

    /// <summary>使用中情境之描述文字（查詢注入來源，spec#9）；無使用中則空字串（＝回歸現行行為）。</summary>
    public string ActiveText() => ActiveText(Load());

    // ---- 圖片 IO ----

    public string ImagePathFor(string fileName) => Path.Combine(_imageDir, fileName);

    /// <summary>寫入某情境圖片（png），回檔名（供設 <see cref="ContextItem.Image"/>）。</summary>
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

    public static ContextItem Add(ContextsData d, string name)
    {
        var item = new ContextItem { Name = string.IsNullOrWhiteSpace(name) ? "新情境" : name.Trim() };
        if (d.Items.Count == 0)
        {
            item.IsActive = true; // 首則預設使用中
        }
        d.Items.Add(item);
        return item;
    }

    public static void Rename(ContextsData d, string id, string name)
    {
        var it = Find(d, id);
        if (it is not null && !string.IsNullOrWhiteSpace(name))
        {
            it.Name = name.Trim();
        }
    }

    public static void UpdateText(ContextsData d, string id, string text)
    {
        var it = Find(d, id);
        if (it is not null)
        {
            it.Text = text ?? "";
        }
    }

    /// <summary>設定單一使用中（其餘取消）。</summary>
    public static void SetActive(ContextsData d, string id)
    {
        foreach (var it in d.Items)
        {
            it.IsActive = it.Id == id;
        }
    }

    public static ContextItem? Find(ContextsData d, string id) => d.Items.FirstOrDefault(i => i.Id == id);

    public static ContextItem? GetActive(ContextsData d) => d.Items.FirstOrDefault(i => i.IsActive);

    public static string ActiveText(ContextsData d) => GetActive(d)?.Text ?? "";

    /// <summary>移除一則；回傳被移除項（供呼叫端刪其圖片）。</summary>
    public static ContextItem? Remove(ContextsData d, string id)
    {
        var it = Find(d, id);
        if (it is not null)
        {
            d.Items.Remove(it);
        }
        return it;
    }

    /// <summary>舊 paramContextHint 相容遷移：清單為空且 hint 非空 → 建「預設情境」設為使用中。回是否有遷移。</summary>
    public static bool Migrate(ContextsData d, string? legacyHint)
    {
        if (d.Items.Count == 0 && !string.IsNullOrWhiteSpace(legacyHint))
        {
            d.Items.Add(new ContextItem { Name = "預設情境", Text = legacyHint.Trim(), IsActive = true });
            return true;
        }
        return false;
    }
}
