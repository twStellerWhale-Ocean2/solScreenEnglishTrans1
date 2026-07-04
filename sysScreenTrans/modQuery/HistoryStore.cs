using System.IO;
using System.Text.Json;

namespace ScreenTrans.Query;

/// <summary>
/// 查詢歷史本機儲存（[modQuery模組] 查詢歷史儲存契約，spec#6）。
/// 存 <c>%APPDATA%\ScreenTrans\history.json</c>（與 <c>ui-state.json</c> 同資料夾、各自檔名）；
/// 新在前、達上限環形截汰最舊；讀寫失敗一律退空清單／靜默降級，不影響查詢主流程；金鑰不入歷史。
/// 排序與截汰邏輯為不依賴 UI／檔案 IO 的純函式（<see cref="Prepend"/>），可單元測試。
/// </summary>
public sealed class HistoryStore
{
    /// <summary>保留筆數預設／下限（非正上限一律套用此值）。</summary>
    public const int DefaultMax = 200;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;

    /// <param name="path">歷史檔路徑；預設 <see cref="DefaultPath"/>（測試可注入暫存路徑）。</param>
    public HistoryStore(string? path = null) => _path = path ?? DefaultPath;

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTrans");

    public static string DefaultPath => Path.Combine(DefaultDir, "history.json");

    /// <summary>讀出歷史（新在前）；缺檔或格式毀損 → 空清單、不致命。</summary>
    public List<HistoryEntry> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch
        {
            return new(); // 缺檔或格式壞 → 退空清單
        }
    }

    /// <summary>純函式：把新紀錄置頂、依上限環形截汰最舊（max ≤ 0 套用 <see cref="DefaultMax"/>）。</summary>
    public static List<HistoryEntry> Prepend(IEnumerable<HistoryEntry> existing, HistoryEntry entry, int max)
    {
        int cap = max > 0 ? max : DefaultMax;
        var list = new List<HistoryEntry> { entry };
        list.AddRange(existing);
        if (list.Count > cap)
        {
            list.RemoveRange(cap, list.Count - cap); // 超限截汰尾端（最舊）
        }
        return list;
    }

    /// <summary>追加一筆查詢結果（新在前、截汰最舊）；寫入失敗靜默降級、不影響查詢主流程。</summary>
    public void Append(QueryResult result, int max, DateTimeOffset now)
    {
        try
        {
            Save(Prepend(Load(), HistoryEntry.From(result, now), max));
        }
        catch { /* 寫入失敗（權限等）不致命 */ }
    }

    /// <summary>刪除單筆（依 Id）；找不到即無動作。</summary>
    public void Delete(string id)
    {
        try
        {
            var list = Load();
            list.RemoveAll(e => e.Id == id);
            Save(list);
        }
        catch { /* 不致命 */ }
    }

    /// <summary>清除全部歷史。</summary>
    public void Clear()
    {
        try { Save(new List<HistoryEntry>()); }
        catch { /* 不致命 */ }
    }

    private void Save(List<HistoryEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(list, Opts));
    }
}
