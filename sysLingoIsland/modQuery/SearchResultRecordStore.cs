using System.IO;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>
/// 影片搜尋成果紀錄（[modQuery模組]，#186）：記每支影片之**初次搜尋時間**，存
/// <c>%APPDATA%\LingoIsland\video-search-records.json</c>（VideoId → ISO 時間字串）。
/// 供搜尋結果表格顯示「初次搜尋時間」欄——同片再次出現於搜尋時沿用初值、不覆寫。
/// 讀寫失敗一律退空／靜默降級，不影響搜尋主流程。
/// </summary>
public sealed class SearchResultRecordStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;

    public SearchResultRecordStore(string? path = null) => _path = path ?? DefaultPath;

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland");

    public static string DefaultPath => Path.Combine(DefaultDir, "video-search-records.json");

    /// <summary>讀出 VideoId→初次搜尋時間（ISO）；缺檔或格式毀損 → 空、不致命。</summary>
    public Dictionary<string, string> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new(); }
        catch { return new(); }
    }

    /// <summary>
    /// 批次「取或記」一組影片之初次搜尋時間：已存者沿用原值、未存者以 <paramref name="now"/> 記錄；一次讀寫、回 VideoId→初次時間。
    /// 純粹合併（<see cref="Merge"/>）與檔案 IO 分離，供單元測試。寫入失敗靜默降級（仍回正確的合併結果）。
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset> RecordAndGet(IReadOnlyList<string> videoIds, DateTimeOffset now)
    {
        var raw = Load();
        var (merged, result, changed) = Merge(raw, videoIds, now);
        if (changed) { try { Save(merged); } catch { /* 不致命 */ } }
        return result;
    }

    /// <summary>純函式：合併——已存 VideoId 沿用原時間、未存者記為 <paramref name="now"/>；回 (更新後 map, VideoId→初次時間, 是否有變更)。internal 供單元測試。</summary>
    internal static (Dictionary<string, string> Merged, Dictionary<string, DateTimeOffset> Result, bool Changed) Merge(
        Dictionary<string, string> existing, IReadOnlyList<string> videoIds, DateTimeOffset now)
    {
        var merged = new Dictionary<string, string>(existing);
        var result = new Dictionary<string, DateTimeOffset>();
        var changed = false;
        foreach (var id in videoIds)
        {
            if (string.IsNullOrEmpty(id)) { continue; }
            if (merged.TryGetValue(id, out var iso) && DateTimeOffset.TryParse(iso, out var t))
            {
                result[id] = t;
            }
            else
            {
                merged[id] = now.ToString("o");
                result[id] = now;
                changed = true;
            }
        }
        return (merged, result, changed);
    }

    public void Clear() { try { Save(new Dictionary<string, string>()); } catch { /* 不致命 */ } }

    private void Save(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(map, Opts));
    }
}
