using System.IO;
using LingoIsland.Video;

namespace LingoIsland.Present;

/// <summary>單一待加入字幕檔之處置狀態（#193）：涵蓋設計 ＜III.C.(C)＞ 所列全部失敗類型，不得只定義 happy path。</summary>
public enum AcquireStatus
{
    /// <summary>可加入：已取得影片 ID、檔案可讀。</summary>
    Ready,
    /// <summary>該影片已有字幕紀錄——批次**預設略過**，惟彙總表可逐列改選覆寫。</summary>
    AlreadyExists,
    /// <summary>檔頭區取不到可解析之 YouTube 影片網址。</summary>
    MissingVideoId,
    /// <summary>兩檔解出同一影片 ID——後者標明，由使用者裁決。</summary>
    DuplicateVideoId,
    /// <summary>副檔名不在白名單，或拖入的是資料夾。</summary>
    Unsupported,
    /// <summary>不存在／無權限／被佔用／過大／空檔（<see cref="AcquireEntry.Detail"/> 載明原因）。</summary>
    Unreadable,
    /// <summary>疑似編碼不符（U+FFFD 逾門檻）——仍可加入，但先警示。</summary>
    Misdecoded,
}

/// <summary>
/// 彙總確認表之一列（#193）：選檔／拖放當下**只做預掃描**（僅讀檔頭區取影片 ID），故 <see cref="CueCount"/>／
/// <see cref="SpeakerCount"/>／<see cref="LastSec"/> 於預掃描階段恆為預設值，**按下主鈕後之全檔解析才填入**——
/// 此分界即設計 ＜III.B.(A)＞「選檔預掃描不阻塞 UI」之責任邊界，不得於選檔當下全檔解析。
/// </summary>
/// <param name="Path">完整路徑（僅內部保存／tooltip；UI 一律只顯 <see cref="FileName"/>，防絕對路徑隨截圖外洩）。</param>
public sealed record AcquireEntry(
    string Path,
    string? VideoId,
    AcquireStatus Status,
    string? Detail = null,
    int CueCount = 0,
    int SpeakerCount = 0,
    double? LastSec = null)
{
    /// <summary>顯示用檔名（不含路徑）。</summary>
    public string FileName => TranscriptFile.SafeName(Path);

    /// <summary>此列是否可實際建立字幕（<see cref="AcquireStatus.AlreadyExists"/> 須使用者改選覆寫才算數，故不在此列）。</summary>
    public bool IsAddable => Status is AcquireStatus.Ready or AcquireStatus.Misdecoded;
}

/// <summary>
/// 【獲得】多檔批次之純函式輔助（[modPresent模組]，#193）：去重、檔數上限、同 ID 衝突標記、狀態文案、彙總與結果摘要。
/// <b>純函式、不碰 UI 與 IO</b>——批次流程之判斷邏輯集中於此以便單元測試，避免 <see cref="VideoCapturePage"/>
/// （已逾 1600 行）再長出流程編排責任。實際讀檔由 <see cref="TranscriptFile"/>、解析由 <see cref="SubtitleParser"/> 負責。
/// </summary>
public static class AcquireBatch
{
    /// <summary>
    /// 併入新選取之檔案（純函式）：**同一完整路徑不重複入列**（清單層去重）；併入後逾
    /// <see cref="TranscriptFile.MaxBatchFiles"/> 者**不默默截斷**——回傳已截至上限之清單，並由 <paramref name="rejected"/>
    /// 回報被拒收之檔數供呼叫端明訊。路徑比對於 Windows 不分大小寫。
    /// </summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string>? existing, IEnumerable<string>? incoming, out int rejected)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (existing ?? Enumerable.Empty<string>()).Concat(incoming ?? Enumerable.Empty<string>()))
        {
            if (string.IsNullOrWhiteSpace(p)) { continue; }
            var key = Normalize(p);
            if (!seen.Add(key)) { continue; }                      // 同路徑重複選取／重複拖入→只留一筆
            list.Add(p);
        }
        rejected = Math.Max(0, list.Count - TranscriptFile.MaxBatchFiles);
        return rejected > 0 ? list.Take(TranscriptFile.MaxBatchFiles).ToList() : list;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>
    /// 標記同 ID 衝突（純函式）：兩個以上檔案解出**同一影片 ID** 時，**後者**改標
    /// <see cref="AcquireStatus.DuplicateVideoId"/> 由使用者裁決（首個保留原狀態）。已為失敗態者不覆寫其狀態。
    /// </summary>
    public static IReadOnlyList<AcquireEntry> MarkDuplicateVideoIds(IReadOnlyList<AcquireEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AcquireEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (e.VideoId is null || e.Status is AcquireStatus.Unsupported or AcquireStatus.Unreadable or AcquireStatus.MissingVideoId)
            {
                result.Add(e);
                continue;
            }
            result.Add(seen.Add(e.VideoId)
                ? e
                : e with { Status = AcquireStatus.DuplicateVideoId, Detail = "與前一檔指向同一支影片" });
        }
        return result;
    }

    /// <summary>時間長度文案（純函式）：秒→<c>M:SS</c>／<c>H:MM:SS</c>。null 或非正值回空字串。</summary>
    public static string FormatLength(double? sec)
    {
        if (!sec.HasValue || sec.Value <= 0) { return ""; }
        var t = TimeSpan.FromSeconds(sec.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>
    /// 單列狀態文案（純函式）——與設計 ＜III.C.(C)＞ 狀態欄一一對應。
    /// **長度於選檔當下即顯示（USR 回饋）且不分狀態**：取自串流掃描之最後時間戳（非全檔解析故不阻塞），
    /// 只要掃得到就附在狀態後面——「已有字幕」「缺影片網址」等狀態同樣看得到長度，使用者才能據以判斷是不是要的那一集。
    /// 句數／說話人數則須按下主鈕實際解析後才有，選檔階段不顯示。
    /// </summary>
    public static string StatusText(AcquireEntry e)
    {
        var len = FormatLength(e.LastSec);
        var suffix = len.Length > 0 ? $" · 長度 約 {len}" : "";
        return e.Status switch
        {
            AcquireStatus.Ready => e.CueCount > 0
                ? $"就緒 · {e.CueCount} 句 · {e.SpeakerCount} 位說話人 · 長度 {len}"
                : len.Length > 0 ? $"就緒{suffix}" : "就緒 · 已取得影片 ID（此檔沒有時間軸）",
            AcquireStatus.AlreadyExists => $"已有字幕（單檔會問覆寫／批次預設略過）{suffix}",
            AcquireStatus.MissingVideoId => $"缺影片網址——檔頭沒有 YouTube 連結{suffix}",
            AcquireStatus.DuplicateVideoId => $"與前一檔指向同一支影片{suffix}",
            AcquireStatus.Unsupported => "不支援的檔案類型",
            AcquireStatus.Unreadable => string.IsNullOrWhiteSpace(e.Detail) ? "無法讀取" : e.Detail!,
            AcquireStatus.Misdecoded => $"疑似編碼不符——請確認內容是否亂碼{suffix}",
            _ => "",
        };
    }

    /// <summary>
    /// 此列按下主鈕後**會被實際處理**（非略過、非失敗）：`Ready`／`Misdecoded` 可直接建立，
    /// `AlreadyExists` 於單檔會問覆寫、於批次可逐列改選覆寫——三者皆計入主鈕之 N 與「可加入」計數。
    /// 與 <see cref="AcquireEntry.IsAddable"/> 的差別：後者只表「可**直接**建立」，不含須使用者裁決者。
    /// </summary>
    public static bool IsActionable(AcquireEntry e)
        => e.IsAddable || e.Status == AcquireStatus.AlreadyExists;

    /// <summary>
    /// 主行動鈕文案（純函式，#193）：**同手勢異結果須前置揭露**——0–1 個可加入者顯「加入並播放」、
    /// N（≥2）者顯「批次加入 N 部」，使用者按下前即知會不會直接播。異常列不計入 N。
    /// </summary>
    public static string ActionButtonText(int addableCount)
        => addableCount >= 2 ? $"＋ 批次加入 {addableCount} 部" : "＋ 加入並播放";

    /// <summary>彙總確認表之文字（純函式）：逐列列檔名／影片 ID／狀態，供載入前一眼確認；末附費用與播放語意。</summary>
    public static string ConfirmText(IReadOnlyList<AcquireEntry> entries)
    {
        var addable = entries.Count(e => e.IsAddable);
        var lines = entries.Select(e =>
        {
            var id = string.IsNullOrEmpty(e.VideoId) ? "—" : e.VideoId;
            return $"{(e.IsAddable ? "✔" : "⚠")} {e.FileName}\n     {id} · {StatusText(e)}";
        });
        return
            $"共 {entries.Count} 個字幕檔，其中 {addable} 個可加入：\n\n" +
            string.Join("\n", lines) +
            "\n\n加入後會出現在「內容」子頁籤的影片清單，**不會自動播放**。\n" +
            "全部走免費解析，不使用 AI、不花 OpenAI 額度。\n\n要加入嗎？";
    }

    /// <summary>批次完成後之結果摘要（純函式）：成功／略過／失敗計數＋逐檔原因；被略過者標補救動線，不讓使用者誤以為該檔無法使用。</summary>
    public static string ResultText(int added, IReadOnlyList<AcquireEntry> skipped, IReadOnlyList<(AcquireEntry Entry, string Reason)> failed)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"批次加入完成：成功 {added} 部");
        if (skipped.Count > 0) { sb.Append($"、略過 {skipped.Count} 個"); }
        if (failed.Count > 0) { sb.Append($"、失敗 {failed.Count} 個"); }
        sb.Append('。');
        if (skipped.Count > 0)
        {
            sb.Append("\n\n略過：\n");
            sb.Append(string.Join("\n", skipped.Select(e => $"· {e.FileName}——{StatusText(e)}")));
            sb.Append("\n（可單獨選取該檔以單檔模式處理。）");
        }
        if (failed.Count > 0)
        {
            sb.Append("\n\n失敗：\n");
            sb.Append(string.Join("\n", failed.Select(f => $"· {f.Entry.FileName}——{f.Reason}")));
        }
        return sb.ToString();
    }
}
