using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LingoIsland.Query;

/// <summary>
/// 影片搜尋字幕狀態快取（[modQuery模組]，#188）：記每支影片探測過的**內嵌字幕（人工/自動）**與**網路字幕**結果，存
/// <c>%APPDATA%\LingoIsland\video-subtitle-status.json</c>（VideoId → 狀態）。搜尋時**還原已知結果、不再重探**——
/// 內嵌探測雖免費但慢（每列一個 yt-dlp 行程），網路探測**會花 OpenAI 額度**：快取讓同片重搜不再重花錢（#188 的核心動機）。
/// 內嵌與網路兩部分**各自獨立更新、互不覆寫**（<see cref="MergeEmbedded"/>／<see cref="MergeWeb"/>）。讀寫失敗一律靜默降級、退回未知（照原路即時探測），不影響搜尋主流程。
/// </summary>
public sealed class VideoSubtitleStatusStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string _path;

    public VideoSubtitleStatusStore(string? path = null) => _path = path ?? DefaultPath;

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland");

    public static string DefaultPath => Path.Combine(DefaultDir, "video-subtitle-status.json");

    /// <summary>網路字幕結果狀態字串（JSON 值）：有＝<c>found</c>、無＝<c>none</c>（未查則 <see cref="Entry.Web"/> 為 null）。</summary>
    public const string WebFound = "found";
    public const string WebNone = "none";

    /// <summary>單支影片之字幕狀態：內嵌人工/自動（bool?，null＝尚未探測成功）、網路（found/none/null）＋來源。</summary>
    public sealed class Entry
    {
        [JsonPropertyName("manual")] public bool? Manual { get; set; }
        [JsonPropertyName("auto")] public bool? Auto { get; set; }
        [JsonPropertyName("web")] public string? Web { get; set; }        // "found" | "none" | null（未查）
        [JsonPropertyName("webSource")] public string? WebSource { get; set; }
    }

    /// <summary>讀出 VideoId→狀態；缺檔或格式毀損 → 空、不致命。</summary>
    public Dictionary<string, Entry> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? new(); }
        catch { return new(); }
    }

    /// <summary>取單支影片狀態；無則 null。</summary>
    public Entry? Get(string videoId)
    {
        var map = Load();
        return map.TryGetValue(videoId, out var e) ? e : null;
    }

    /// <summary>記/更新內嵌字幕探測結果（保留既有網路部分）；寫入失敗靜默降級。</summary>
    public void SaveEmbedded(string videoId, bool hasManual, bool hasAuto)
    {
        if (string.IsNullOrEmpty(videoId)) { return; }
        var map = Load();
        MergeEmbedded(map, videoId, hasManual, hasAuto);
        TrySave(map);
    }

    /// <summary>批次記/更新多支影片之內嵌結果（#188）：一次讀寫，避免併發逐列探測各自 Load→Save 互相覆蓋；保留各自既有網路部分。</summary>
    public void SaveEmbeddedBatch(IEnumerable<(string VideoId, bool HasManual, bool HasAuto)> items)
    {
        var list = items.Where(i => !string.IsNullOrEmpty(i.VideoId)).ToList();
        if (list.Count == 0) { return; }
        var map = Load();
        foreach (var it in list) { MergeEmbedded(map, it.VideoId, it.HasManual, it.HasAuto); }
        TrySave(map);
    }

    /// <summary>記/更新網路字幕探測結果（保留既有內嵌部分）；寫入失敗靜默降級。</summary>
    public void SaveWeb(string videoId, bool found, string? source)
    {
        if (string.IsNullOrEmpty(videoId)) { return; }
        var map = Load();
        MergeWeb(map, videoId, found, source);
        TrySave(map);
    }

    // ── 純函式合併（internal 供單元測試）：各自更新一半、保留另一半 ──

    /// <summary>純函式：更新內嵌部分（人工/自動），保留既有網路部分。回傳同一 <paramref name="map"/>（就地更新）。</summary>
    internal static Dictionary<string, Entry> MergeEmbedded(Dictionary<string, Entry> map, string videoId, bool hasManual, bool hasAuto)
    {
        if (!map.TryGetValue(videoId, out var e)) { e = new Entry(); map[videoId] = e; }
        e.Manual = hasManual;
        e.Auto = hasAuto;
        return map;
    }

    /// <summary>純函式：更新網路部分（found/none＋來源），保留既有內嵌部分。回傳同一 <paramref name="map"/>（就地更新）。</summary>
    internal static Dictionary<string, Entry> MergeWeb(Dictionary<string, Entry> map, string videoId, bool found, string? source)
    {
        if (!map.TryGetValue(videoId, out var e)) { e = new Entry(); map[videoId] = e; }
        e.Web = found ? WebFound : WebNone;
        e.WebSource = found ? source : null;
        return map;
    }

    private void TrySave(Dictionary<string, Entry> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(map, Opts));
        }
        catch { /* 不致命：下次搜尋照原路即時探測 */ }
    }
}
