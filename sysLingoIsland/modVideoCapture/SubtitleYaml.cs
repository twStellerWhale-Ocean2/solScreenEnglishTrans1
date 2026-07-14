using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LingoIsland.Video;

/// <summary>
/// 字幕整檔 YAML 序列化（[modVideoCapture模組]，epic #145 增量5，#154）：把逐句 <see cref="SubtitleCue"/> 與
/// 整份 YAML 文件互轉，供影片頁「整檔 YAML 編修」模式——使用者一次編修整份字幕（合併/拆分斷句、標註說話人），
/// 較逐行編修更利於處理斷行問題。純函式、不依賴 UI，可單元測試。
/// 格式（start-only #158）：cue 清單，每項 <c>speaker</c>（可空＝未標示）／<c>start</c>（秒）／<c>text</c>；不含結束時間。
/// </summary>
public static class SubtitleYaml
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance) // speaker / start / text
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties() // 使用者手打多餘鍵不致命
        .Build();

    /// <summary>逐句 cue → YAML 文件（speaker/start/text 依此序；未標示 speaker 留空供填寫）。空清單回空字串。</summary>
    public static string Serialize(IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count == 0) return "";
        var rows = cues.Select(c => new CueYaml
        {
            Speaker = c.Speaker,
            Start = Math.Round(c.StartSec, 3),
            Text = c.Text,
        }).ToList();
        return Serializer.Serialize(rows);
    }

    /// <summary>
    /// YAML 文件 → 逐句 cue（start-only #158）。空文字之項略過；未標示 speaker（空白）＝null。依起點排序。
    /// YAML 語法錯誤擲 <see cref="SubtitleException"/>（含首行原因），供 UI 明訊、不中斷程式。
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Parse(string? yaml)
    {
        var cues = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(yaml)) return cues;

        List<CueYaml>? rows;
        try
        {
            rows = Deserializer.Deserialize<List<CueYaml>>(yaml);
        }
        catch (YamlException ex)
        {
            throw new SubtitleException("Invalid YAML: " + FirstLine(ex.Message));
        }
        catch (Exception ex)
        {
            // 使用者手打之非預期結構（根為對映、型別無法轉換等）——一律轉可讀失敗，供 UI 明訊、不成為未觀察 task 例外
            throw new SubtitleException("Could not parse subtitle YAML: " + FirstLine(ex.Message));
        }
        if (rows is null) return cues;

        foreach (var r in rows)
        {
            var text = (r.Text ?? "").Trim();
            if (text.Length == 0) continue; // 空文字略過
            var speaker = string.IsNullOrWhiteSpace(r.Speaker) ? null : r.Speaker.Trim();
            cues.Add(new SubtitleCue(text, r.Start, speaker));
        }
        // 依起點穩定排序：PauseDecider（NextPause/CueAt）假定 StartSec 遞增；使用者於整檔 YAML 可能改動時序或重排
        return cues.OrderBy(c => c.StartSec).ToList();
    }

    private static string FirstLine(string s)
    {
        var line = s.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "parse error";
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    /// <summary>YAML 每項對映（camelCase 鍵）；Start 為秒（start-only #158，無 end）。</summary>
    private sealed class CueYaml
    {
        public string? Speaker { get; set; }
        public double Start { get; set; }
        public string? Text { get; set; }
    }
}
