using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LingoIsland.Present;

/// <summary>英文句切分後的一個 token：原句子字串片段（<see cref="Text"/>）＋是否為可點單字（<see cref="IsWord"/>）。</summary>
public readonly record struct WordToken(string Text, bool IsWord);

/// <summary>
/// 英文句逐字切分（Issue #11，present 段純函式——不依賴 WPF、可單元測試）。
/// 把原句切為交錯的「單字」與「分隔（空白／標點）」token：單字＝字母／數字序列，
/// 內部撇號（<c>'</c> <c>’</c>）與連字號（<c>-</c>）於兩側皆為字母數字時併入單字；
/// 前後標點不入單字（剝除），單字內部撇號／連字號與大小寫原樣保留。
/// 所有 token 之 <see cref="WordToken.Text"/> 依序串接必等於原字串（切分不遺失任何字元）。
/// </summary>
public static partial class EnglishWordTokenizer
{
    // 單字＝一段字母/數字，允許內部以撇號或連字號銜接下一段字母/數字（前後標點自然被排除）。
    [GeneratedRegex(@"[A-Za-z0-9]+(?:['’\-][A-Za-z0-9]+)*")]
    private static partial Regex WordRegex();

    /// <summary>切分為交錯的單字／分隔 token；null 或空字串回傳空清單。</summary>
    public static IReadOnlyList<WordToken> Tokenize(string? text)
    {
        var tokens = new List<WordToken>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        int pos = 0;
        foreach (Match m in WordRegex().Matches(text))
        {
            if (m.Index > pos)
            {
                tokens.Add(new WordToken(text[pos..m.Index], false)); // 單字前的分隔（空白/標點）
            }
            tokens.Add(new WordToken(m.Value, true)); // 單字（前後標點已剝除、內部撇號/連字號保留）
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
        {
            tokens.Add(new WordToken(text[pos..], false)); // 結尾分隔
        }
        return tokens;
    }
}
