using System.IO;
using System.Text.Json;

namespace ScreenTrans.Query;

/// <summary>一個自訂類別資料夾：Id＋名稱＋<b>子資料夾</b>（多層）＋有序條目清單（新在前）。</summary>
public sealed class NoteFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>子資料夾（多層樹；Issue #34）。舊平面 notes.json 無此鍵 → 反序列化為空、即單層。</summary>
    public List<NoteFolder> Folders { get; set; } = new();
    public List<NoteEntry> Entries { get; set; } = new();
}

/// <summary>我的筆記根結構：頂層資料夾清單（每個可再含子資料夾）。</summary>
public sealed class NotesData
{
    public List<NoteFolder> Folders { get; set; } = new();
}

/// <summary>加入我的筆記之結果（供 toast 回饋）。</summary>
public enum NoteAddResult { Added, AlreadyExists, Empty }

/// <summary>
/// 我的筆記本機儲存（[modQuery模組] 我的筆記儲存契約，spec#7；Issue #34 樹化）。存
/// <c>%APPDATA%\ScreenTrans\notes.json</c>。資料夾為**多層樹**（向後相容舊平面）；加入以英文原文正規化
/// 跨全樹去重；資料夾 CRUD（含子夾）、條目排序、節點移動（防移入自身/子孫成環）皆為不依賴 UI 之純函式、可單元測試。
/// 讀取失敗退空結構、寫入失敗靜默降級——皆不致命；金鑰不入筆記；不受歷史清除影響。
/// </summary>
public sealed class NotesStore
{
    public const string DefaultFolderName = "My Notes";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;

    public NotesStore(string? path = null) => _path = path ?? DefaultPath;

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTrans");

    public static string DefaultPath => Path.Combine(DefaultDir, "notes.json");

    public NotesData Load()
    {
        try
        {
            return JsonSerializer.Deserialize<NotesData>(File.ReadAllText(_path)) ?? new NotesData();
        }
        catch
        {
            return new NotesData();
        }
    }

    public NotesData LoadEnsured()
    {
        var d = Load();
        Ensure(d);
        return d;
    }

    public static void Ensure(NotesData d)
    {
        if (d.Folders.Count == 0)
        {
            d.Folders.Add(new NoteFolder { Name = DefaultFolderName });
        }
    }

    public void Save(NotesData d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(d, Opts));
        }
        catch { /* 寫入失敗不影響主流程 */ }
    }

    public NoteAddResult AddAndSave(QueryResult r, DateTimeOffset now)
    {
        var d = LoadEnsured();
        var res = AddTo(d, NoteEntry.From(r, now));
        if (res == NoteAddResult.Added)
        {
            Save(d);
        }
        return res;
    }

    /// <summary>
    /// 加入至**指定名稱之頂層資料夾**並套底色（Issue #55）：<paramref name="folderName"/> 空則退回預設夾行為
    /// （第一個頂層夾）；找不到同名頂層夾則建立。跨全樹去重（同 <see cref="AddAndSave"/> 語意），
    /// 加入時設 <paramref name="colorHex"/>（空＝無底色）。回 <see cref="NoteAddResult"/>。
    /// </summary>
    public NoteAddResult AddToNamedFolderAndSave(QueryResult r, string? folderName, string? colorHex, DateTimeOffset now)
    {
        var d = LoadEnsured();
        var entry = NoteEntry.From(r, now) with { Color = colorHex ?? "" };
        var res = string.IsNullOrWhiteSpace(folderName)
            ? AddTo(d, entry) // 空名＝預設夾（第一個頂層）
            : AddToTopFolder(d, EnsureTopFolderByName(d, folderName!), entry);
        if (res == NoteAddResult.Added)
        {
            Save(d);
        }
        return res;
    }

    // ---- 純函式（樹感知，可單元測試） ----

    /// <summary>樹的前序走訪（含所有子資料夾）。</summary>
    public static IEnumerable<NoteFolder> AllFolders(NotesData d) => Walk(d.Folders);

    private static IEnumerable<NoteFolder> Walk(IEnumerable<NoteFolder> folders)
    {
        foreach (var f in folders)
        {
            yield return f;
            foreach (var s in Walk(f.Folders))
            {
                yield return s;
            }
        }
    }

    public static NoteFolder? FindFolder(NotesData d, string id) =>
        AllFolders(d).FirstOrDefault(f => f.Id == id);

    /// <summary>某去重鍵是否已存在於樹中任一資料夾。</summary>
    public static bool Contains(NotesData d, string key) =>
        !string.IsNullOrEmpty(key) && AllFolders(d).Any(f => f.Entries.Any(e => e.Key == key));

    /// <summary>加入至第一個頂層資料夾頂端；空原文回 Empty、已存在（跨全樹去重）回 AlreadyExists。</summary>
    public static NoteAddResult AddTo(NotesData d, NoteEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Key))
        {
            return NoteAddResult.Empty;
        }
        if (Contains(d, entry.Key))
        {
            return NoteAddResult.AlreadyExists;
        }
        Ensure(d);
        d.Folders[0].Entries.Insert(0, entry);
        return NoteAddResult.Added;
    }

    /// <summary>取得指定名稱之頂層資料夾（Issue #55）；不存在則於頂層新建並回傳。名稱空白退回預設夾名。</summary>
    public static NoteFolder EnsureTopFolderByName(NotesData d, string name)
    {
        var target = string.IsNullOrWhiteSpace(name) ? DefaultFolderName : name.Trim();
        var existing = d.Folders.FirstOrDefault(f => string.Equals(f.Name, target, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }
        var created = new NoteFolder { Name = target };
        d.Folders.Add(created);
        return created;
    }

    /// <summary>加入至指定頂層資料夾頂端（Issue #55）；空原文回 Empty、跨全樹去重已存在回 AlreadyExists。</summary>
    public static NoteAddResult AddToTopFolder(NotesData d, NoteFolder folder, NoteEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Key))
        {
            return NoteAddResult.Empty;
        }
        if (Contains(d, entry.Key))
        {
            return NoteAddResult.AlreadyExists;
        }
        folder.Entries.Insert(0, entry);
        return NoteAddResult.Added;
    }

    /// <summary>
    /// 建立資料夾之預設名（檔案總管慣例，Issue #38）：`新資料夾`，已占用則 `新資料夾 (2)`、`(3)`…
    /// 全樹唯一（建立後隨即進入原地更名，故只需避免同名混淆）。
    /// </summary>
    public static string NextNewFolderName(NotesData d)
    {
        var names = AllFolders(d).Select(f => f.Name).ToHashSet();
        const string baseName = "New Folder";
        if (!names.Contains(baseName))
        {
            return baseName;
        }
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 檔案總管式**自然排序**比較（Issue #42，等效 `StrCmpLogicalW` 核心行為）：
    /// 連續數字段依**數值**比較（「新資料夾 (2)」＜「新資料夾 (10)」），其餘字元依目前文化、不分大小寫。
    /// </summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) { i++; }
                while (j < b.Length && char.IsAsciiDigit(b[j])) { j++; }
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length)
                {
                    return na.Length - nb.Length; // 位數多者數值大
                }
                var c = string.CompareOrdinal(na, nb);
                if (c != 0)
                {
                    return c;
                }
            }
            else
            {
                var c = string.Compare(a[i].ToString(), b[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
                if (c != 0)
                {
                    return c;
                }
                i++;
                j++;
            }
        }
        return (a.Length - i) - (b.Length - j); // 前綴相同者短在前
    }

    /// <summary>全樹**同層依名稱自然排序**（檔案總管慣例，Issue #42）：拖曳只改歸屬、順序一律由名稱決定。</summary>
    public static void SortFolders(NotesData d) => SortSiblings(d.Folders);

    private static void SortSiblings(List<NoteFolder> list)
    {
        list.Sort((x, y) => NaturalCompare(x.Name, y.Name));
        foreach (var f in list)
        {
            SortSiblings(f.Folders);
        }
    }

    /// <summary>清空指定資料夾之全部條目（不含子資料夾；右欄[清除全部]，Issue #42）。找不到夾即無為。</summary>
    public static void ClearEntries(NotesData d, string folderId) => FindFolder(d, folderId)?.Entries.Clear();

    /// <summary>
    /// 設定條目底色（Issue #44）：跨全樹依 Id 尋得後以 record `with` 換置；<paramref name="color"/>
    /// 為 hex 字串、空＝預設白。找不到條目回 false。
    /// </summary>
    public static bool SetEntryColor(NotesData d, string entryId, string color)
    {
        foreach (var f in AllFolders(d))
        {
            var i = f.Entries.FindIndex(e => e.Id == entryId);
            if (i >= 0)
            {
                f.Entries[i] = f.Entries[i] with { Color = color ?? "" };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 設定條目發音練習分數（spec#10）：跨全樹依 Id 尋得後以 record <c>with</c> 換置 <see cref="NoteEntry.PracticeScore"/>；
    /// 找不到條目回 false。分數 -1＝未練；燈泡是否點亮由呈現層以「分數 ≥ 及格門檻」判定。
    /// </summary>
    public static bool SetPracticeScore(NotesData d, string entryId, int score)
    {
        foreach (var f in AllFolders(d))
        {
            var i = f.Entries.FindIndex(e => e.Id == entryId);
            if (i >= 0)
            {
                f.Entries[i] = f.Entries[i] with { PracticeScore = score };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 清空指定資料夾之發音練習紀錄（spec#10）：該夾所有條目 <see cref="NoteEntry.PracticeScore"/> 歸 -1
    /// （燈泡全滅），子夾與他夾不動。取代原「清除全部（刪筆記）」之 UI 入口——本法**不刪筆記、只重置練習**。
    /// 找不到夾即無為。純函式、可單元測試。
    /// </summary>
    public static void ResetFolderPractice(NotesData d, string folderId)
    {
        var f = FindFolder(d, folderId);
        if (f is null)
        {
            return;
        }
        for (var i = 0; i < f.Entries.Count; i++)
        {
            f.Entries[i] = f.Entries[i] with { PracticeScore = -1 };
        }
    }

    /// <summary>新增頂層資料夾。</summary>
    public static NoteFolder AddFolder(NotesData d, string name)
    {
        var f = new NoteFolder { Name = Clean(name) };
        d.Folders.Add(f);
        return f;
    }

    /// <summary>於指定父夾下新增子資料夾（找不到父夾回 null）。</summary>
    public static NoteFolder? AddSubFolder(NotesData d, string parentId, string name)
    {
        var p = FindFolder(d, parentId);
        if (p is null)
        {
            return null;
        }
        var f = new NoteFolder { Name = Clean(name) };
        p.Folders.Add(f);
        return f;
    }

    public static void RenameFolder(NotesData d, string id, string name)
    {
        var f = FindFolder(d, id);
        if (f is not null && !string.IsNullOrWhiteSpace(name))
        {
            f.Name = name.Trim();
        }
    }

    /// <summary>刪除資料夾（連其子孫與條目）；刪後確保仍有預設資料夾。</summary>
    public static void RemoveFolder(NotesData d, string id)
    {
        RemoveFolderById(d.Folders, id);
        Ensure(d);
    }

    private static bool RemoveFolderById(List<NoteFolder> list, string id)
    {
        var i = list.FindIndex(f => f.Id == id);
        if (i >= 0)
        {
            list.RemoveAt(i);
            return true;
        }
        foreach (var f in list)
        {
            if (RemoveFolderById(f.Folders, id))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>自樹中任一資料夾刪除指定條目。</summary>
    public static void RemoveEntry(NotesData d, string entryId)
    {
        foreach (var f in AllFolders(d))
        {
            f.Entries.RemoveAll(e => e.Id == entryId);
        }
    }

    /// <summary>將條目移動到目標資料夾頂端（跨夾歸類）。</summary>
    public static void MoveEntry(NotesData d, string entryId, string toFolderId)
    {
        var to = FindFolder(d, toFolderId);
        if (to is null)
        {
            return;
        }
        foreach (var f in AllFolders(d))
        {
            var idx = f.Entries.FindIndex(e => e.Id == entryId);
            if (idx >= 0)
            {
                if (ReferenceEquals(f, to))
                {
                    return;
                }
                var e = f.Entries[idx];
                f.Entries.RemoveAt(idx);
                to.Entries.Insert(0, e);
                return;
            }
        }
    }

    /// <summary>
    /// 移動資料夾節點到新父夾（<paramref name="toParentId"/> 為 null＝移到頂層）。
    /// **防成環**：不得移入自身或其子孫；找不到節點/父夾回 false。
    /// </summary>
    public static bool MoveFolder(NotesData d, string folderId, string? toParentId)
    {
        var node = FindFolder(d, folderId);
        if (node is null)
        {
            return false;
        }
        List<NoteFolder> target;
        if (toParentId is null)
        {
            target = d.Folders;
        }
        else
        {
            var p = FindFolder(d, toParentId);
            if (p is null || p.Id == folderId || IsDescendant(node, p.Id))
            {
                return false; // 移入自身或子孫 → 拒絕（防環）
            }
            target = p.Folders;
        }
        if (!DetachFolder(d.Folders, node))
        {
            return false;
        }
        target.Add(node);
        return true;
    }

    private static bool IsDescendant(NoteFolder node, string maybeDescendantId) =>
        Walk(node.Folders).Any(f => f.Id == maybeDescendantId);

    private static bool DetachFolder(List<NoteFolder> list, NoteFolder node)
    {
        if (list.Remove(node))
        {
            return true;
        }
        foreach (var f in list)
        {
            if (DetachFolder(f.Folders, node))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 依英文原文自然排序目前資料夾之條目（Issue #52）：<paramref name="ascending"/>＝順向（A→Z）、否則反向（Z→A）。
    /// 沿用 <see cref="NaturalCompare"/>（大小寫不敏感、數字段依數值），與資料夾排序一致；重排後由呼叫端 Save 持久化。
    /// 空夾即無為。純函式、可單元測試。
    /// </summary>
    public static void SortEntries(NoteFolder f, bool ascending)
    {
        f.Entries.Sort((x, y) =>
        {
            var c = NaturalCompare(x.Original ?? "", y.Original ?? "");
            return ascending ? c : -c;
        });
    }

    /// <summary>同一資料夾內把條目自 from 位置移到 to 位置（拖曳排序）。</summary>
    public static void Reorder(NoteFolder f, int from, int to)
    {
        if (from < 0 || from >= f.Entries.Count)
        {
            return;
        }
        to = Math.Max(0, Math.Min(to, f.Entries.Count - 1));
        if (from == to)
        {
            return;
        }
        var e = f.Entries[from];
        f.Entries.RemoveAt(from);
        f.Entries.Insert(to, e);
    }

    private static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? "New Folder" : name.Trim();
}
