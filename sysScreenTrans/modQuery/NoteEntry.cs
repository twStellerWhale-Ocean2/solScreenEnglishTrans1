using System.Text.Json.Serialization;

namespace ScreenTrans.Query;

/// <summary>
/// 單筆我的筆記（[modQuery模組] 我的筆記儲存契約，spec#7）：唯一 Id＋加入時間＋三欄結果＋底色
/// （<see cref="Color"/>，hex 字串、空＝預設白，Issue #44；舊檔缺欄位由建構子預設值相容）。
/// 去重以 <see cref="Key"/>（英文原文正規化：去頭尾空白、大小寫折疊）為準；金鑰不入筆記。發音練習**最佳分**
/// <see cref="PracticeScore"/>（-1＝未練、≥設定門檻＝成績框綠底＋✓，spec#10；取歷來最大、舊檔缺欄位由建構子預設 -1 相容）。
/// </summary>
public sealed record NoteEntry(string Id, DateTimeOffset AddedAt, string Original, string Phonetic, string Translation, string Color = "", int PracticeScore = -1)
{
    /// <summary>還原為呈現層三欄結果（供筆記「檢視」開結果卡片詳情）。</summary>
    public QueryResult ToResult() => new(Original, Phonetic, Translation);

    /// <summary>由一筆查詢結果與當下時間建立筆記（Id 隨機、供定位；底色預設空＝白）。</summary>
    public static NoteEntry From(QueryResult r, DateTimeOffset now) =>
        new(Guid.NewGuid().ToString("N"), now, r.Original, r.Phonetic, r.Translation);

    /// <summary>去重鍵：英文原文去頭尾空白＋大小寫折疊。</summary>
    public static string KeyOf(string? original) => (original ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>本筆之去重鍵（不序列化、由 Original 導出）。</summary>
    [JsonIgnore]
    public string Key => KeyOf(Original);
}
