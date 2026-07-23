using System.IO;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>一筆已加入之影片（epic #145 增量4，spec#2）：YouTube 影片 ID＋標題＋加入時間＋使用中主題快照（跨媒體主題歸屬）。</summary>
public sealed class VideoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string VideoId { get; set; } = "";   // YouTube 11 碼影片 ID
    public string Title { get; set; } = "";      // 顯示標題（自播放器取得；未取得前退 VideoId）
    public string? ThemeId { get; set; }
    public string? ThemeName { get; set; }
    public string AddedAt { get; set; } = "";     // ISO 8601
    public double? DurationSec { get; set; }      // #207：時間長度（秒）——載入字幕以推估先落、起播後播放器實測覆蓋；舊檔無值＝null
}

/// <summary>影片清單根結構（新在前）。</summary>
public sealed class VideosData
{
    public List<VideoItem> Items { get; set; } = new();
    public string? SortKey { get; set; }          // #207：影片欄排序鍵（AddedNew|Title|Duration|Theme）；null＝AddedNew（插入序新在前）
}

/// <summary>
/// 影片清單本機儲存（[modQuery模組]，epic #145 增量4）：存 <c>%APPDATA%\LingoIsland\videos.json</c>。
/// <see cref="Add"/> 依 <see cref="VideoItem.VideoId"/> 去重（既有則移至最前並更新標題/主題/時間）；<see cref="Remove"/>／<see cref="UpdateTitle"/>。
/// 清單增刪為純函式（可單元測試，不觸檔案）；讀寫失敗降級不致命。
/// </summary>
public sealed class VideoStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;

    public VideoStore(string? path = null) { _path = path ?? DefaultPath; }

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland");

    public static string DefaultPath => Path.Combine(DefaultDir, "videos.json");

    public VideosData Load()
    {
        try { return JsonSerializer.Deserialize<VideosData>(File.ReadAllText(_path)) ?? new VideosData(); }
        catch { return new VideosData(); }
    }

    public void Save(VideosData d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(d, Opts));
        }
        catch { /* 寫入失敗不影響主流程 */ }
    }

    /// <summary>加入／更新一筆影片：依 VideoId 去重（既有則移至最前、更新標題/主題/時間）、存回。回該項。</summary>
    public VideoItem Add(string videoId, string title, string? themeId, string? themeName, DateTimeOffset addedAt)
    {
        var d = Load();
        var item = Upsert(d, videoId, title, themeId, themeName, addedAt.ToString("o"));
        Save(d);
        return item;
    }

    public VideoItem? Remove(string id)
    {
        var d = Load();
        var it = RemoveFromList(d, id);
        if (it is not null) { Save(d); }
        return it;
    }

    /// <summary>清空影片清單（#165 Clear all）。</summary>
    public void Clear() => Save(new VideosData());

    /// <summary>回寫某項標題（自播放器取得標題後）。</summary>
    public void UpdateTitle(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) { return; }
        var d = Load();
        var it = d.Items.FirstOrDefault(i => i.Id == id);
        if (it is not null) { it.Title = title.Trim(); Save(d); }
    }

    /// <summary>回寫某項所屬主題（內容區塊主題下拉重指派，#173）；<paramref name="themeId"/>＝null＝改為未歸屬。</summary>
    public void UpdateTheme(string id, string? themeId, string? themeName)
    {
        var d = Load();
        if (SetTheme(d, id, themeId, themeName)) { Save(d); }
    }

    /// <summary>回寫某項時間長度（#207）：<paramref name="estimateOnly"/>＝true＝字幕推估、僅該項尚無值才落；false＝播放器實測、一律覆蓋。非正值不寫。</summary>
    public void UpdateDuration(string id, double durationSec, bool estimateOnly = false)
    {
        if (durationSec <= 0) { return; }
        var d = Load();
        var it = d.Items.FirstOrDefault(i => i.Id == id);
        if (it is null) { return; }
        if (estimateOnly && it.DurationSec is > 0) { return; } // 已有值（含實測）不被推估退化
        it.DurationSec = durationSec;
        Save(d);
    }

    /// <summary>回寫影片欄排序鍵（#207）；跨啟動沿用。</summary>
    public void UpdateSortKey(string? sortKey)
    {
        var d = Load();
        d.SortKey = sortKey;
        Save(d);
    }

    // ---- 純函式（可單元測試，不觸檔案） ----

    /// <summary>依 VideoId 去重加入：既有則移至最前並更新標題（非空才更新）/主題/時間；新則以標題（空退 VideoId）插入最前。回該項。</summary>
    public static VideoItem Upsert(VideosData d, string videoId, string title, string? themeId, string? themeName, string addedAt)
    {
        var existing = d.Items.FirstOrDefault(i => i.VideoId == videoId);
        if (existing is not null)
        {
            d.Items.Remove(existing);
            if (!string.IsNullOrWhiteSpace(title)) { existing.Title = title; }
            existing.ThemeId = themeId;
            existing.ThemeName = themeName;
            existing.AddedAt = addedAt;
            d.Items.Insert(0, existing);
            return existing;
        }
        var item = new VideoItem
        {
            VideoId = videoId,
            Title = string.IsNullOrWhiteSpace(title) ? videoId : title,
            ThemeId = themeId,
            ThemeName = themeName,
            AddedAt = addedAt,
        };
        d.Items.Insert(0, item);
        return item;
    }

    public static VideoItem? RemoveFromList(VideosData d, string id)
    {
        var it = d.Items.FirstOrDefault(i => i.Id == id);
        if (it is not null) { d.Items.Remove(it); }
        return it;
    }

    /// <summary>設定某項所屬主題（純函式，#173）：找到即改 ThemeId／ThemeName（名稱去空白、空白視為 null）、回 true；無此 id 回 false。</summary>
    public static bool SetTheme(VideosData d, string id, string? themeId, string? themeName)
    {
        var it = d.Items.FirstOrDefault(i => i.Id == id);
        if (it is null) { return false; }
        it.ThemeId = themeId;
        it.ThemeName = string.IsNullOrWhiteSpace(themeName) ? null : themeName.Trim();
        return true;
    }

    /// <summary>
    /// 影片欄排序（#207，純函式、穩定排序、不改動傳入序）：呈現層投影、清單仍存插入序。
    /// `AddedNew`（null／未知鍵＝預設）＝插入序（新在前，即現況）；`Title`＝標題自然排序（沿筆記 <see cref="NotesStore.NaturalCompare"/> 家規：大小寫不敏感、數字段依數值）；
    /// `Duration`＝短→長、無值排末；`Theme`＝主題名自然排序群組（未歸屬排末）、組內維持插入序。
    /// </summary>
    public static List<VideoItem> SortVideos(IEnumerable<VideoItem> items, string? sortKey)
    {
        var list = items.ToList();
        return sortKey switch
        {
            "Title" => list.OrderBy(i => i.Title ?? "", NaturalTitleComparer.Instance).ToList(),
            "Duration" => list.OrderBy(i => i.DurationSec is > 0 ? 0 : 1).ThenBy(i => i.DurationSec ?? double.MaxValue).ToList(),
            "Theme" => list.OrderBy(i => string.IsNullOrWhiteSpace(i.ThemeName) ? 1 : 0).ThenBy(i => i.ThemeName ?? "", NaturalTitleComparer.Instance).ToList(),
            _ => list, // AddedNew／null／未知鍵＝插入序
        };
    }

    /// <summary>#207：包 <see cref="NotesStore.NaturalCompare"/> 為 IComparer（供 OrderBy 穩定排序用）。</summary>
    private sealed class NaturalTitleComparer : IComparer<string>
    {
        public static readonly NaturalTitleComparer Instance = new();
        public int Compare(string? x, string? y) => NotesStore.NaturalCompare(x ?? "", y ?? "");
    }
}
