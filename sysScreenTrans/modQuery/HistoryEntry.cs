namespace ScreenTrans.Query;

/// <summary>
/// 單筆查詢歷史（[modQuery模組] 查詢歷史儲存契約，spec#6）：唯一 Id＋時間戳＋三欄結果。
/// 金鑰不入歷史；<see cref="ToResult"/> 供歷史「檢視」重用結果視窗之三欄詳情/發音。
/// </summary>
public sealed record HistoryEntry(string Id, DateTimeOffset Timestamp, string Original, string Phonetic, string Translation)
{
    /// <summary>還原為呈現層三欄結果（供歷史「檢視」開結果卡片詳情）。</summary>
    public QueryResult ToResult() => new(Original, Phonetic, Translation);

    /// <summary>由一筆查詢結果與當下時間建立歷史紀錄（Id 為隨機值、供刪除定位）。</summary>
    public static HistoryEntry From(QueryResult r, DateTimeOffset now) =>
        new(Guid.NewGuid().ToString("N"), now, r.Original, r.Phonetic, r.Translation);
}
