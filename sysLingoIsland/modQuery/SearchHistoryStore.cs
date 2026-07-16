using System.IO;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>
/// 影片搜尋關鍵字歷史（[modQuery模組]，#186）：存 <c>%APPDATA%\LingoIsland\video-search-history.json</c>（純字串清單、新在前）。
/// 供搜尋框下拉顯示、可逐筆刪除。去重（不分大小寫）＋截汰最舊之邏輯為純函式（<see cref="Prepend"/>），可單元測試；
/// 讀寫失敗一律退空清單／靜默降級，不影響搜尋主流程。
/// </summary>
public sealed class SearchHistoryStore
{
    public const int DefaultMax = 50;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;

    public SearchHistoryStore(string? path = null) => _path = path ?? DefaultPath;

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland");

    public static string DefaultPath => Path.Combine(DefaultDir, "video-search-history.json");

    /// <summary>讀出歷史（新在前）；缺檔或格式毀損 → 空清單、不致命。</summary>
    public List<string> Load()
    {
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? new(); }
        catch { return new(); }
    }

    /// <summary>純函式：把 <paramref name="query"/> 置頂、移除既有同值（不分大小寫、去重）、依上限截汰最舊（max ≤ 0 套 <see cref="DefaultMax"/>）；空白 query 不加。</summary>
    public static List<string> Prepend(IEnumerable<string> existing, string? query, int max)
    {
        var cap = max > 0 ? max : DefaultMax;
        var q = (query ?? "").Trim();
        var list = new List<string>();
        if (q.Length > 0) { list.Add(q); }
        foreach (var e in existing)
        {
            if (!string.Equals(e, q, StringComparison.OrdinalIgnoreCase)) { list.Add(e); }
        }
        if (list.Count > cap) { list.RemoveRange(cap, list.Count - cap); }
        return list;
    }

    /// <summary>加入一筆搜尋關鍵字（置頂去重、截汰最舊）；空白略過；寫入失敗靜默降級。</summary>
    public void Add(string? query)
    {
        try
        {
            var q = (query ?? "").Trim();
            if (q.Length == 0) { return; }
            Save(Prepend(Load(), q, DefaultMax));
        }
        catch { /* 不致命 */ }
    }

    /// <summary>刪除某筆關鍵字（不分大小寫）；找不到即無動作。</summary>
    public void Delete(string query)
    {
        try
        {
            var list = Load();
            list.RemoveAll(e => string.Equals(e, query, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }
        catch { /* 不致命 */ }
    }

    public void Clear() { try { Save(new List<string>()); } catch { /* 不致命 */ } }

    private void Save(List<string> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(list, Opts));
    }
}
