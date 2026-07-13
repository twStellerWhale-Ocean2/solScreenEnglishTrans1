namespace LingoIsland.Query;

/// <summary>
/// 查詢結果三欄（[datIntf自訂查詢結果格式]）。三欄皆必要（string）；
/// 三欄皆空字串＝選區無可辨識英文，呈現層顯示「未偵測到英文文字」。
/// <see cref="SuggestedColor"/>（Issue #55）＝智能配色下 AI 依使用者規則建議之底色 hex（空＝無建議），
/// 非核心三欄、預設空以保回歸；僅在有配色規則時填。
/// </summary>
public sealed record QueryResult(string Original, string Phonetic, string Translation, string SuggestedColor = "")
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Original)
                        && string.IsNullOrWhiteSpace(Phonetic)
                        && string.IsNullOrWhiteSpace(Translation);
}
