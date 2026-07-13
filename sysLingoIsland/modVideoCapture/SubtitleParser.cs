using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LingoIsland.Video;

/// <summary>
/// 字幕解析（[techItem字幕擷取]／[modVideoCapture模組] 影片擷取契約，spec#2）：把 VTT／SRT 字幕文字
/// 解析為逐句 <see cref="SubtitleCue"/>（文字＋起訖秒）。不依賴 UI／網路、純函式、可單元測試。
/// 容錯：時間軸可含或不含小時位、逗號或句點毫秒分隔；剝除 VTT／HTML 標籤（<c>、行內 &lt;00:00:01.500&gt; 等）
/// 與常見 HTML 實體；多行字幕以空白併行；去除連續完全重複之字幕（YouTube 自動字幕滾動重複）；空文字略過。
/// </summary>
public static class SubtitleParser
{
    private static readonly Regex TimeLine = new(
        @"(?<a>(?:\d+:)?\d{1,2}:\d{2}[.,]\d{3})\s*-->\s*(?<b>(?:\d+:)?\d{1,2}:\d{2}[.,]\d{3})",
        RegexOptions.Compiled);
    private static readonly Regex Tag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    /// <summary>解析字幕全文為逐句 cue（依出現順序、去連續重複、空文字略過）。null／無時間軸回空清單。</summary>
    public static IReadOnlyList<SubtitleCue> Parse(string? content)
    {
        var cues = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(content)) return cues;

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var m = TimeLine.Match(lines[i]);
            if (!m.Success) { i++; continue; }
            var start = ParseTime(m.Groups["a"].Value);
            var end = ParseTime(m.Groups["b"].Value);
            i++;

            var sb = new StringBuilder();
            while (i < lines.Length && lines[i].Trim().Length > 0 && !TimeLine.IsMatch(lines[i]))
            {
                var clean = Clean(lines[i]);
                if (clean.Length > 0)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(clean);
                }
                i++;
            }

            var text = Ws.Replace(sb.ToString(), " ").Trim();
            if (text.Length == 0 || end <= start) continue;
            if (cues.Count > 0)
            {
                var prev = cues[^1];
                if (text == prev.Text) continue;                                           // 完全重複
                if (text.StartsWith(prev.Text, StringComparison.Ordinal))                  // 滾動延伸（後句含前句）→ 以較完整者取代、延長結束時間
                {
                    cues[^1] = prev with { Text = text, EndSec = end };
                    continue;
                }
                if (prev.Text.StartsWith(text, StringComparison.Ordinal)) continue;        // 為前句之較短前綴 → 略過
            }
            cues.Add(new SubtitleCue(text, start, end));
        }
        return cues;
    }

    private static string Clean(string s)
    {
        s = Tag.Replace(s, "");
        s = s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
             .Replace("&#39;", "'").Replace("&quot;", "\"").Replace("&nbsp;", " ");
        return s.Trim();
    }

    /// <summary>解析 <c>HH:MM:SS.mmm</c>／<c>MM:SS.mmm</c>（逗號或句點毫秒）為秒。internal 供測試。</summary>
    internal static double ParseTime(string t)
    {
        t = t.Replace(',', '.');
        var parts = t.Split(':');
        double h = 0, min, sec;
        if (parts.Length == 3)
        {
            h = double.Parse(parts[0], CultureInfo.InvariantCulture);
            min = double.Parse(parts[1], CultureInfo.InvariantCulture);
            sec = double.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        else
        {
            min = double.Parse(parts[0], CultureInfo.InvariantCulture);
            sec = double.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        return h * 3600 + min * 60 + sec;
    }
}
