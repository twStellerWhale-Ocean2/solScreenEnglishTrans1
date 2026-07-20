using System;
using System.IO;
using System.Linq;
using System.Text;
using LingoIsland.Present;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 本機自製字幕檔匯入（#193）之純函式與 IO 邊界：<see cref="TranscriptFile"/>（編碼／檔頭區／白名單／錯誤路徑）與
/// <see cref="AcquireBatch"/>（去重／檔數上限／同 ID 衝突／狀態文案／主鈕文案）。
/// 樣本取自 [USR] 實際自製檔之代表性片段（檔頭註解行、`NAME:` 與 `A/B:` 說話人前綴、音效標記），
/// 補足既有測試多以自造樣本為主、缺真實世界字幕檔覆蓋之缺口（見 Issue #193 ＜I＞ 測試作法檢討）。
/// </summary>
public class LocalSubtitleFileTests
{
    // [USR] 實際自製檔之代表性片段（peppa_s01e03_best_friend.srt）
    private const string RealSample =
        "; YouTube URL: https://www.youtube.com/watch?v=E2MPOr2g0zg\n" +
        "; Transcript source: https://peppapig.fandom.com/wiki/Best_Friend/Transcript\n" +
        "\n" +
        "1\n00:00:02,535 --> 00:00:04,437\nPeppa: I'm Peppa Pig. (SNORTS)\n" +
        "\n" +
        "2\n00:00:12,078 --> 00:00:13,245\nSound: (ALL LAUGHING)\n" +
        "\n" +
        "3\n00:00:34,734 --> 00:00:38,771\nPeppa/Suzy: (SNORTS) Hello, Suzy. -(BLEATS) Hello, Peppa.\n" +
        "\n" +
        "4\n00:00:55,788 --> 00:00:59,625\nMummy Pig: Peppa, why don't you and Suzy go and play?\n";

    // ── TranscriptFile：檔頭區 ──

    [Fact]
    public void HeaderOf_StopsAtFirstTimeLine_SoDialogueLinksAreNotPicked()
    {
        // 檔頭區＝第一個時間行之前：影片網址抓得到，台詞區之連結一律照不到（防誤採推廣連結／字幕組署名）
        var withDialogueLink = RealSample.Replace(
            "Peppa: I'm Peppa Pig. (SNORTS)",
            "Peppa: go to https://www.youtube.com/watch?v=AAAAAAAAAAA now");
        var header = TranscriptFile.HeaderOf(withDialogueLink);

        Assert.Contains("E2MPOr2g0zg", header);
        Assert.DoesNotContain("AAAAAAAAAAA", header);
    }

    [Fact]
    public void HeaderOf_NoTimeLine_FallsBackToFirstLines_NotWholeFile()
    {
        // 全檔無時間行（尚未加時間碼之逐字稿）→ 只取前 N 行，仍不掃全檔
        var lines = Enumerable.Range(1, 60).Select(i => $"line {i}");
        var text = string.Join("\n", lines) + "\nhttps://youtu.be/ZZZZZZZZZZZ";
        var header = TranscriptFile.HeaderOf(text);

        Assert.DoesNotContain("ZZZZZZZZZZZ", header);
        Assert.Equal(TranscriptFile.HeaderFallbackLines, header.Split('\n').Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HeaderOf_Empty_ReturnsEmpty(string? text) => Assert.Equal("", TranscriptFile.HeaderOf(text));

    /// <summary>檔頭自帶影片網址＋既有 URL 抽取＝可取得影片 ID（#193 之「一個檔就是一課」核心）。</summary>
    [Fact]
    public void HeaderUrl_YieldsVideoId_ViaExistingPureFunctions()
    {
        var header = TranscriptFile.HeaderOf(RealSample);
        var id = VideoCapturePage.ExtractUrls(header).Select(VideoCapturePage.ExtractVideoId).FirstOrDefault(v => v is not null);

        Assert.Equal("E2MPOr2g0zg", id);
    }

    /// <summary>逐字稿出處註記（非 YouTube）不得被誤採為影片；程式亦不會去連它。</summary>
    [Fact]
    public void TranscriptSourceLine_IsNotMistakenForVideo()
    {
        var header = TranscriptFile.HeaderOf(RealSample);
        var ids = VideoCapturePage.ExtractUrls(header).Select(VideoCapturePage.ExtractVideoId).Where(v => v is not null).ToList();

        Assert.Single(ids);
        Assert.Equal("E2MPOr2g0zg", ids[0]);
    }

    // ── TranscriptFile：編碼 ──

    [Fact]
    public void Decode_Utf8WithAndWithoutBom_RoundTrips()
    {
        const string text = "Peppa: I'm Peppa Pig. 中文也要正確";
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        Assert.Equal(text, TranscriptFile.Decode(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, TranscriptFile.Decode(withBom));
    }

    [Fact]
    public void Decode_Utf16Boms_RoundTrip()
    {
        const string text = "Suzy: Hello, Peppa.";
        Assert.Equal(text, TranscriptFile.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()));
        Assert.Equal(text, TranscriptFile.Decode(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray()));
    }

    [Fact]
    public void LooksMisdecoded_Big5BytesReadAsUtf8_IsDetected_WithoutThrowing()
    {
        // 以 UTF-8 解 Big5 位元組**不會擲例外**、只產生 U+FFFD——故判準必須是替代字元比例（設計 ＜III.B.(A)＞ 定值）
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var big5 = Encoding.GetEncoding(950).GetBytes("這是一份中文字幕檔內容，用來測試編碼判定是否成立");

        var decoded = TranscriptFile.Decode(big5);

        Assert.True(TranscriptFile.LooksMisdecoded(decoded), "以 UTF-8 解 Big5 應被判為疑似編碼不符");
    }

    [Fact]
    public void LooksMisdecoded_CleanText_IsFalse()
    {
        Assert.False(TranscriptFile.LooksMisdecoded(RealSample));
        Assert.False(TranscriptFile.LooksMisdecoded("純中文字幕內容，完全正常"));
        Assert.False(TranscriptFile.LooksMisdecoded(""));
    }

    // ── TranscriptFile：白名單與錯誤路徑 ──

    [Theory]
    [InlineData("a.srt", true)]
    [InlineData("a.SRT", true)]
    [InlineData("a.vtt", true)]
    [InlineData("a.txt", true)]
    [InlineData("a.docx", false)]
    [InlineData("a.mp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasAllowedExtension_IsCaseInsensitiveWhitelist(string? path, bool expected)
        => Assert.Equal(expected, TranscriptFile.HasAllowedExtension(path));

    [Fact]
    public void Read_MissingFile_ThrowsHumanReadable_MentioningFileNameOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), "lingoisland-no-such-file-193.srt");
        var ex = Assert.Throws<SubtitleException>(() => TranscriptFile.Read(path));

        Assert.Contains("lingoisland-no-such-file-193.srt", ex.Message);
        Assert.Contains("找不到", ex.Message);
    }

    [Fact]
    public void Read_EmptyFile_ThrowsHumanReadable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lingoisland-empty-{Guid.NewGuid():N}.srt");
        File.WriteAllText(path, "");
        try
        {
            var ex = Assert.Throws<SubtitleException>(() => TranscriptFile.Read(path));
            Assert.Contains("空的", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_RealSample_ReturnsRawIncludingHeaderComments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lingoisland-sample-{Guid.NewGuid():N}.srt");
        File.WriteAllText(path, RealSample, new UTF8Encoding(true));
        try
        {
            var raw = TranscriptFile.Read(path);
            Assert.Contains("; YouTube URL:", raw);          // 原文原樣回傳、不預先剝除任何東西
            Assert.Contains("Peppa/Suzy:", raw);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Decode_Utf32Boms_RoundTrip()
    {
        const string text = "George: (SNORTS)";
        var le = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        var be = new UTF32Encoding(bigEndian: true, byteOrderMark: true);

        Assert.Equal(text, TranscriptFile.Decode(le.GetPreamble().Concat(le.GetBytes(text)).ToArray()));
        Assert.Equal(text, TranscriptFile.Decode(be.GetPreamble().Concat(be.GetBytes(text)).ToArray()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void Decode_NullOrEmpty_ReturnsEmpty(byte[]? bytes) => Assert.Equal("", TranscriptFile.Decode(bytes!));

    [Fact]
    public void Read_OverSizeLimit_ThrowsWithSizeAndLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lingoisland-big-{Guid.NewGuid():N}.srt");
        File.WriteAllBytes(path, new byte[TranscriptFile.MaxBytes + 1]);
        try
        {
            var ex = Assert.Throws<SubtitleException>(() => TranscriptFile.Read(path));
            Assert.Contains("過大", ex.Message);
            Assert.Contains("上限", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Read_NoPath_Throws(string? path)
        => Assert.Throws<SubtitleException>(() => TranscriptFile.Read(path!));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ReadHeader_NoPath_Throws(string? path)
        => Assert.Throws<SubtitleException>(() => TranscriptFile.ReadHeader(path!));

    [Fact]
    public void ReadHeader_MissingFile_ThrowsHumanReadable()
    {
        var path = Path.Combine(Path.GetTempPath(), "lingoisland-no-such-header-193.srt");
        var ex = Assert.Throws<SubtitleException>(() => TranscriptFile.ReadHeader(path));
        Assert.Contains("找不到", ex.Message);
    }

    [Fact]
    public void ReadHeader_DirectoryPath_ThrowsHumanReadable_NotRawIoException()
    {
        // 拖入資料夾之保底（UI 已先以白名單擋下，此為 IO 層不當機之保證）
        var ex = Assert.Throws<SubtitleException>(() => TranscriptFile.ReadHeader(Path.GetTempPath()));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void ReadHeader_StopsAtTimeLine_ReadingOnlyTheCommentBlock()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lingoisland-hdr-{Guid.NewGuid():N}.srt");
        File.WriteAllText(path, RealSample, new UTF8Encoding(false));
        try
        {
            var header = TranscriptFile.ReadHeader(path);
            Assert.Contains("E2MPOr2g0zg", header);
            Assert.DoesNotContain("-->", header);            // 串流讀取確實停在第一個時間行
            Assert.DoesNotContain("Peppa: I'm Peppa Pig", header);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SafeName_EmptyPath_HasPlaceholder() => Assert.Equal("（未命名）", TranscriptFile.SafeName(""));

    [Fact]
    public void SafeName_ReturnsFileNameOnly_SoAbsolutePathNeverLeaks()
    {
        // UI 與錯誤訊息只顯檔名（絕對路徑含使用者名稱，README 須嵌真實截圖故不得外洩）
        Assert.Equal("best_friend.srt", TranscriptFile.SafeName(@"C:\Users\SomeRealName\Videos\best_friend.srt"));
    }

    // ── 端到端（純函式串接）：真實樣本 → cue ──

    [Fact]
    public void RealSample_ParsesFreely_WithSpeakers_AndCombinedSpeakerSplit()
    {
        var parsed = SubtitleParser.Parse(RealSample);
        var cues = SubtitleParser.ExtractInlineSpeakers(parsed);

        Assert.Equal(4, cues.Count);                                   // 檔頭兩行註解被自然略過、不成句
        Assert.All(cues, c => Assert.True(c.StartSec.HasValue));       // 自帶時間軸→免費解析即可，無須 AI
        Assert.Equal("Peppa", cues[0].Speaker);
        Assert.Equal("I'm Peppa Pig. (SNORTS)", cues[0].Text);
        Assert.Equal("Sound", cues[1].Speaker);
        Assert.Equal("Mummy Pig", cues[3].Speaker);                    // 多詞單名不誤拆

        // #193 核心修補：合唸說話人抽得出來，且下游拆得出原子說話人
        Assert.Equal("Peppa/Suzy", cues[2].Speaker);
        Assert.Equal(new[] { "Peppa", "Suzy" }, PauseDecider.SplitSpeakers(cues[2].Speaker).ToArray());
    }

    [Theory]
    [InlineData("Tom & Jerry: Hello there.", "Tom & Jerry")]
    [InlineData("Narrator/Sound: So does George.", "Narrator/Sound")]
    [InlineData("Daddy Pig/Peppa/Suzy: And me!", "Daddy Pig/Peppa/Suzy")]
    public void CombinedSpeakers_AreExtracted(string line, string expected)
    {
        var cues = SubtitleParser.ExtractInlineSpeakers(new[] { new SubtitleCue(line, 1.0) });
        Assert.Equal(expected, cues[0].Speaker);
    }

    [Theory]
    [InlineData("Yes, Mummy: I'm coming.")]          // 逗號為句子標點——刻意不納入字元類，不得誤判
    [InlineData("well: lowercase start")]            // 非大寫開頭
    [InlineData("This is a very long sentence that just happens to have: a colon")] // 逾 3 詞
    public void NonSpeakerLines_AreNotMistakenForSpeakers(string line)
    {
        var cues = SubtitleParser.ExtractInlineSpeakers(new[] { new SubtitleCue(line, 1.0) });
        Assert.Null(cues[0].Speaker);
        Assert.Equal(line, cues[0].Text);
    }

    /// <summary>
    /// **防退化斷言（#193 設計審查發現）**：免費解析路徑必須直吃 raw——若先跑 <see cref="TranscriptAlign.StripToPlainText"/>，
    /// 其 `AnyTag` 會刪除 VTT 語音標記 `&lt;v 名字&gt;`，而 <see cref="SubtitleParser.Parse"/> 正靠該標記取說話人 → VTT 檔說話人全滅。
    /// </summary>
    [Fact]
    public void VttVoiceTagSpeakers_SurviveFreeParse_ButAreLostIfStrippedFirst()
    {
        const string vtt =
            "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n<v Peppa>I'm Peppa Pig.\n\n" +
            "00:00:04.000 --> 00:00:06.000\n<v Suzy Sheep>Hello, Peppa.\n";

        var direct = SubtitleParser.Parse(vtt);
        Assert.Equal(new[] { "Peppa", "Suzy Sheep" }, direct.Select(c => c.Speaker).ToArray());

        var stripped = SubtitleParser.Parse(TranscriptAlign.StripToPlainText(vtt));
        Assert.All(stripped, c => Assert.Null(c.Speaker));   // 證明「先 Strip 再 Parse」確實會弄丟說話人
    }

    // ── AcquireBatch：純函式 ──

    [Fact]
    public void Merge_DedupesSamePath_AndKeepsOrder()
    {
        var merged = AcquireBatch.Merge(new[] { @"C:\a\1.srt" }, new[] { @"C:\a\1.srt", @"C:\a\2.srt" }, out var rejected);

        Assert.Equal(new[] { @"C:\a\1.srt", @"C:\a\2.srt" }, merged);
        Assert.Equal(0, rejected);
    }

    [Fact]
    public void Merge_OverCap_TruncatesButReportsRejectedCount()
    {
        var many = Enumerable.Range(1, TranscriptFile.MaxBatchFiles + 7).Select(i => $@"C:\a\{i}.srt");
        var merged = AcquireBatch.Merge(null, many, out var rejected);

        Assert.Equal(TranscriptFile.MaxBatchFiles, merged.Count);
        Assert.Equal(7, rejected);   // 不默默截斷——呼叫端須明訊告知
    }

    [Fact]
    public void MarkDuplicateVideoIds_FlagsLaterOnesOnly()
    {
        var entries = new[]
        {
            new AcquireEntry(@"C:\a\1.srt", "AAAAAAAAAAA", AcquireStatus.Ready),
            new AcquireEntry(@"C:\a\2.srt", "BBBBBBBBBBB", AcquireStatus.Ready),
            new AcquireEntry(@"C:\a\3.srt", "AAAAAAAAAAA", AcquireStatus.Ready),
        };

        var marked = AcquireBatch.MarkDuplicateVideoIds(entries);

        Assert.Equal(AcquireStatus.Ready, marked[0].Status);
        Assert.Equal(AcquireStatus.Ready, marked[1].Status);
        Assert.Equal(AcquireStatus.DuplicateVideoId, marked[2].Status);
    }

    [Fact]
    public void MarkDuplicateVideoIds_LeavesFailedEntriesAlone()
    {
        var entries = new[]
        {
            new AcquireEntry(@"C:\a\1.docx", null, AcquireStatus.Unsupported),
            new AcquireEntry(@"C:\a\2.srt", null, AcquireStatus.MissingVideoId),
        };

        var marked = AcquireBatch.MarkDuplicateVideoIds(entries);

        Assert.Equal(AcquireStatus.Unsupported, marked[0].Status);
        Assert.Equal(AcquireStatus.MissingVideoId, marked[1].Status);
    }

    [Theory]
    [InlineData(0, "＋ 加入並播放")]
    [InlineData(1, "＋ 加入並播放")]
    [InlineData(2, "＋ 批次加入 2 部")]
    [InlineData(26, "＋ 批次加入 26 部")]
    public void ActionButtonText_DisclosesModeBeforePress(int addable, string expected)
        => Assert.Equal(expected, AcquireBatch.ActionButtonText(addable));

    [Fact]
    public void IsAddable_ExcludesAlreadyExistsAndFailures()
    {
        Assert.True(new AcquireEntry("a", "id", AcquireStatus.Ready).IsAddable);
        Assert.True(new AcquireEntry("a", "id", AcquireStatus.Misdecoded).IsAddable);   // 亂碼仍可加入、只是先警示
        Assert.False(new AcquireEntry("a", "id", AcquireStatus.AlreadyExists).IsAddable); // 須使用者改選覆寫才算數
        Assert.False(new AcquireEntry("a", null, AcquireStatus.MissingVideoId).IsAddable);
        Assert.False(new AcquireEntry("a", null, AcquireStatus.Unsupported).IsAddable);
    }

    [Fact]
    public void StatusText_CoversEveryStatus_NoBlankFallThrough()
    {
        foreach (AcquireStatus s in Enum.GetValues<AcquireStatus>())
        {
            var text = AcquireBatch.StatusText(new AcquireEntry("a.srt", "id", s, Detail: "詳細原因"));
            Assert.False(string.IsNullOrWhiteSpace(text), $"狀態 {s} 沒有對應文案");
        }
    }

    [Fact]
    public void ConfirmText_StatesNoAutoPlay_AndNoAiCost()
    {
        var text = AcquireBatch.ConfirmText(new[]
        {
            new AcquireEntry(@"C:\a\1.srt", "AAAAAAAAAAA", AcquireStatus.Ready),
            new AcquireEntry(@"C:\a\2.docx", null, AcquireStatus.Unsupported),
        });

        Assert.Contains("1.srt", text);
        Assert.DoesNotContain(@"C:\a", text);          // 彙總表亦只顯檔名
        Assert.Contains("不會自動播放", text);
        Assert.Contains("不使用 AI", text);
    }

    [Fact]
    public void ResultText_ListsSkippedWithRecoveryHint()
    {
        var skipped = new[] { new AcquireEntry(@"C:\a\3.srt", "CCCCCCCCCCC", AcquireStatus.AlreadyExists) };
        var text = AcquireBatch.ResultText(2, skipped, Array.Empty<(AcquireEntry, string)>());

        Assert.Contains("成功 2 部", text);
        Assert.Contains("3.srt", text);
        Assert.Contains("單檔模式", text);   // 被略過者須有補救動線，不讓使用者以為該檔不能用
    }
}
