using System.IO;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>一筆保存之螢幕截圖（epic #145 增量3，spec#1）：檔名＋擷取時間＋擷取當下使用中主題快照（跨媒體主題歸屬）。</summary>
public sealed class ScreenshotItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string File { get; set; } = "";       // {id}.png（存於 screenshots\）
    public string CapturedAt { get; set; } = "";  // ISO 8601（供顯示/排序）
    public string? ThemeId { get; set; }          // 擷取當下使用中主題（可空）
    public string? ThemeName { get; set; }        // 主題名快照（免解析、供顯示）
}

/// <summary>截圖清單根結構（新在前）。</summary>
public sealed class ScreenshotsData
{
    public List<ScreenshotItem> Items { get; set; } = new();
}

/// <summary>
/// 螢幕截圖本機儲存（[modQuery模組]，epic #145 增量3）：每次擷取保存 PNG 於 <c>%APPDATA%\LingoIsland\screenshots\</c>，
/// 清單於 <c>screenshots.json</c>（新在前）。<see cref="Add"/> 超上限自動汰除最舊（連同刪 PNG）；<see cref="Remove"/>／<see cref="Clear"/>。
/// 清單增刪為純函式（可單元測試，不觸檔案）；讀寫失敗降級不致命。
/// </summary>
public sealed class ScreenshotStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _imageDir;
    private readonly int _max;

    public ScreenshotStore(string? path = null, string? imageDir = null, int max = 100)
    {
        _path = path ?? DefaultPath;
        _imageDir = imageDir ?? DefaultImageDir;
        _max = max < 1 ? 100 : max;
    }

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland");

    public static string DefaultPath => Path.Combine(DefaultDir, "screenshots.json");
    public static string DefaultImageDir => Path.Combine(DefaultDir, "screenshots");

    public ScreenshotsData Load()
    {
        try { return JsonSerializer.Deserialize<ScreenshotsData>(File.ReadAllText(_path)) ?? new ScreenshotsData(); }
        catch { return new ScreenshotsData(); }
    }

    public void Save(ScreenshotsData d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(d, Opts));
        }
        catch { /* 寫入失敗不影響主流程 */ }
    }

    public string ImagePathFor(string fileName) => Path.Combine(_imageDir, fileName);

    /// <summary>保存一張截圖：寫 PNG、於清單最前插入、超上限汰除最舊（連同刪其 PNG）、存回。回新項。</summary>
    public ScreenshotItem Add(byte[] png, string? themeId, string? themeName, DateTimeOffset capturedAt)
    {
        var d = Load();
        var item = new ScreenshotItem
        {
            CapturedAt = capturedAt.ToString("o"),
            ThemeId = themeId,
            ThemeName = themeName,
        };
        item.File = item.Id + ".png";
        try
        {
            Directory.CreateDirectory(_imageDir);
            File.WriteAllBytes(Path.Combine(_imageDir, item.File), png);
        }
        catch { /* 寫圖失敗仍記清單（縮圖顯無圖） */ }

        var evicted = AddToList(d, item, _max);
        foreach (var e in evicted) { DeleteImage(e.File); }
        Save(d);
        return item;
    }

    public ScreenshotItem? Remove(string id)
    {
        var d = Load();
        var it = RemoveFromList(d, id);
        if (it is not null)
        {
            DeleteImage(it.File);
            Save(d);
        }
        return it;
    }

    public void Clear()
    {
        var d = Load();
        foreach (var it in d.Items) { DeleteImage(it.File); }
        d.Items.Clear();
        Save(d);
    }

    private void DeleteImage(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) { return; }
        try { File.Delete(Path.Combine(_imageDir, fileName)); }
        catch { /* 不致命 */ }
    }

    // ---- 純函式（可單元測試，不觸檔案） ----

    /// <summary>於清單最前插入 <paramref name="item"/>、超 <paramref name="max"/> 自末端（最舊）汰除；回被汰除項（供呼叫端刪其 PNG）。</summary>
    public static List<ScreenshotItem> AddToList(ScreenshotsData d, ScreenshotItem item, int max)
    {
        d.Items.Insert(0, item); // 新在前
        var evicted = new List<ScreenshotItem>();
        while (d.Items.Count > max)
        {
            evicted.Add(d.Items[^1]);
            d.Items.RemoveAt(d.Items.Count - 1);
        }
        return evicted;
    }

    /// <summary>自清單移除指定 id；回被移除項（供呼叫端刪其 PNG）、無則 null。</summary>
    public static ScreenshotItem? RemoveFromList(ScreenshotsData d, string id)
    {
        var it = d.Items.FirstOrDefault(i => i.Id == id);
        if (it is not null) { d.Items.Remove(it); }
        return it;
    }
}
