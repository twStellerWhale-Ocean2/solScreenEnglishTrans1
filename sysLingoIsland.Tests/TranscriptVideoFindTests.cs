using System.Text.Json;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組]「由字幕檔網址配影片」純函式（TranscriptVideoFind，epic #178 增量2〔由逐字稿改造〕，#182 兩階段）：
/// 第1階段組「列連結」提示＋解析 <c>{links:[{title, transcript_url}]}</c>（單檔頁退用輸入網址、連結去重濾失效、缺欄/malformed/圍籬容錯）；
/// 第2階段組「驗證單份＋配片」提示＋解析單一 <c>{title, youtube_url, source, transcript_url, has_speaker, sample}</c>（驗證含說話人、採傳入 transcript_url、youtube ID 抽取）；
/// 另含純文字含說話人啟發式、連結正規化去重、自 URL 取影片 ID、合輯判別。皆以假 Responses JSON 餵測、不打真網路。
/// </summary>
public class TranscriptVideoFindTests
{
    /// <summary>把 JSON 文字包成 OpenAI Responses API 回應形狀（web_search_call ＋ message.output_text）。</summary>
    private static string WebApi(string outputText) =>
        JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                new { type = "web_search_call", id = "ws_1", status = "completed" },
                new { type = "message", role = "assistant", content = new object[]
                    { new { type = "output_text", text = outputText } } },
            },
        });

    /// <summary>第1階段：把 links 陣列包成模型輸出之 JSON 字串。</summary>
    private static string LinksJson(params object[] links) => JsonSerializer.Serialize(new { links });

    /// <summary>第1階段一筆連結；以命名參數覆寫需要的欄位。</summary>
    private static object Link(string title = "Ep", string transcript_url = "https://scripts.example.com/ep1")
        => new { title, transcript_url };

    /// <summary>第2階段：把單筆候選包成模型輸出之 JSON 字串（單一物件、非陣列）。</summary>
    private static string OneJson(object one) => JsonSerializer.Serialize(one);

    /// <summary>第2階段一筆合格字幕檔（含說話人樣本）之預設條目；以命名參數覆寫需要的欄位。</summary>
    private static object Entry(string title = "Ep", string youtube_url = "", string source = "src",
        string transcript_url = "https://scripts.example.com/ep1", bool has_speaker = true,
        string sample = "Ryder: PAW Patrol, to the Lookout!\nChase: Chase is on the case!")
        => new { title, youtube_url, source, transcript_url, has_speaker, sample };

    // ── 第1階段：BuildListLinksPrompt（列連結；單檔頁↔目錄頁二擇，只列連結不判斷說話人） ──

    [Fact]
    public void BuildListLinksPrompt_InstructsListLinks_IncludesUrlMaxAndKeys()
    {
        var p = TranscriptVideoFind.BuildListLinksPrompt("https://scripts.example.com/pawpatrol", 5);
        Assert.Contains("https://scripts.example.com/pawpatrol", p);
        Assert.Contains("5", p);                 // 最多 max 份
        Assert.Contains("目錄頁", p);            // 單檔／目錄頁二擇
        Assert.Contains("links", p);             // 要求的 JSON 鍵
        Assert.Contains("transcript_url", p);
        Assert.DoesNotContain("has_speaker", p); // 第1階段先不判斷說話人（下一步做）
        Assert.DoesNotContain("youtube_url", p); // 第1階段先不配片
    }

    // ── 第2階段：BuildValidateOnePrompt（開單份、驗證含說話人、配 YouTube） ──

    [Fact]
    public void BuildValidateOnePrompt_InstructsValidateAndMatch_IncludesUrlTitleThemeFields()
    {
        var p = TranscriptVideoFind.BuildValidateOnePrompt("https://scripts.example.com/ep1", "PAW Patrol S1E1", "Kids cartoons");
        Assert.Contains("https://scripts.example.com/ep1", p);
        Assert.Contains("PAW Patrol S1E1", p);   // title 輔助辨識
        Assert.Contains("說話人", p);            // 驗證含說話人
        Assert.Contains("Kids cartoons", p);     // 主題供配對參考
        Assert.Contains("youtube_url", p);
        Assert.Contains("has_speaker", p);
        Assert.Contains("sample", p);
    }

    [Fact]
    public void BuildValidateOnePrompt_NoTitleNoTheme_OmitsThem()
    {
        var p = TranscriptVideoFind.BuildValidateOnePrompt("https://a.example.com/ep");
        Assert.Contains("https://a.example.com/ep", p);
        Assert.DoesNotContain("所屬主題", p);
        Assert.DoesNotContain("其標題約為", p);
    }

    // ── ExtractVideoId（internal） ──

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/abc123DEF_-", "abc123DEF_-")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]                       // 裸 11 碼
    [InlineData("https://example.com/no-id-here", null)]
    [InlineData("", null)]
    [InlineData("not a url", null)]
    public void ExtractVideoId_ParsesLinksAndBareIds(string input, string? expected)
        => Assert.Equal(expected, TranscriptVideoFind.ExtractVideoId(input));

    // ── HasSpeakerMarkup（#182：純文字含說話人啟發式） ──

    [Fact]
    public void HasSpeakerMarkup_TwoOrMoreSpeakerColonLines_True()
        => Assert.True(TranscriptVideoFind.HasSpeakerMarkup("Ryder: PAW Patrol, to the Lookout!\nChase: Chase is on the case!"));

    [Fact]
    public void HasSpeakerMarkup_FullWidthColon_True()
        => Assert.True(TranscriptVideoFind.HasSpeakerMarkup("阿寶：你好嗎\n毛毛：我很好"));

    [Fact]
    public void HasSpeakerMarkup_VttVoiceTag_True()
        => Assert.True(TranscriptVideoFind.HasSpeakerMarkup("00:01.000 --> 00:03.000\n<v Ryder>PAW Patrol, to the Lookout!"));

    [Fact]
    public void HasSpeakerMarkup_SingleSpeakerLine_False()
        => Assert.False(TranscriptVideoFind.HasSpeakerMarkup("Ryder: PAW Patrol, to the Lookout!\nThe pups all run to the elevator."));

    [Fact]
    public void HasSpeakerMarkup_PlainNarration_False()
        => Assert.False(TranscriptVideoFind.HasSpeakerMarkup("The pups run to the Lookout.\nAdventure Bay needs their help."));

    [Fact]
    public void HasSpeakerMarkup_UrlsNotMistakenForSpeakers_False()
        => Assert.False(TranscriptVideoFind.HasSpeakerMarkup("https://youtu.be/dQw4w9WgXcQ\nhttp://scripts.example.com/ep1"));

    [Fact]
    public void HasSpeakerMarkup_TimestampsNotSpeakers_False()
        => Assert.False(TranscriptVideoFind.HasSpeakerMarkup("00:12 hello there\n00:15 how are you"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasSpeakerMarkup_BlankOrNull_False(string? text)
        => Assert.False(TranscriptVideoFind.HasSpeakerMarkup(text));

    // ── NormalizeTranscriptUrl / IsUsableTranscriptUrl（#182：濾失效鍵） ──

    [Theory]
    [InlineData("https://scripts.example.com/ep1", true)]
    [InlineData("http://scripts.example.com/ep1", true)]
    [InlineData("ftp://scripts.example.com/ep1", false)] // 非 http/https
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUsableTranscriptUrl_OnlyAbsoluteHttp(string? url, bool usable)
        => Assert.Equal(usable, TranscriptVideoFind.IsUsableTranscriptUrl(url));

    [Fact]
    public void NormalizeTranscriptUrl_StripsWwwSchemeTrailingSlashFragment()
    {
        var a = TranscriptVideoFind.NormalizeTranscriptUrl("https://WWW.Example.com/Scripts/ep1/#top");
        var b = TranscriptVideoFind.NormalizeTranscriptUrl("http://example.com/Scripts/ep1");
        Assert.Equal(a, b); // host 小寫去 www、去尾斜線、丟 fragment、忽略 scheme → 同鍵
    }

    [Fact]
    public void NormalizeTranscriptUrl_QueryDistinguishes()
    {
        var a = TranscriptVideoFind.NormalizeTranscriptUrl("https://a.example.com/p?id=1");
        var b = TranscriptVideoFind.NormalizeTranscriptUrl("https://a.example.com/p?id=2");
        Assert.NotEqual(a, b);
    }

    // ── DedupLinks（#182：連結去重＋濾失效） ──

    [Fact]
    public void DedupLinks_RemovesDupesAndDeadLinks_PreservesOrderAndOriginal()
    {
        var input = new[]
        {
            "https://a.example.com/x",
            "https://www.a.example.com/x/",   // 同資源（去 www、去尾斜線）→ 去重
            "https://a.example.com/y",
            "bad", "",                          // 畸形／空白 → 濾除
            "ftp://a.example.com/z",            // 非 http/https → 濾除
        };
        var outp = TranscriptVideoFind.DedupLinks(input);
        Assert.Equal(new[] { "https://a.example.com/x", "https://a.example.com/y" }, outp);
    }

    // ── 第1階段解析：ParseTranscriptLinks（目錄頁多連結、單檔頁退用輸入網址、去重濾失效、容錯） ──

    [Fact]
    public void ParseTranscriptLinks_ParsesTitleAndUrl_Multi()
    {
        var text = LinksJson(
            Link("PAW Patrol S1E1", "https://pawpatrol.fandom.com/ep1"),
            Link("PAW Patrol S1E2", "https://pawpatrol.fandom.com/ep2"));
        var list = TranscriptVideoFind.ParseTranscriptLinks(WebApi(text));
        Assert.Equal(2, list.Count);
        Assert.Equal("PAW Patrol S1E1", list[0].Title);
        Assert.Equal("https://pawpatrol.fandom.com/ep1", list[0].TranscriptUrl);
        Assert.Equal("PAW Patrol S1E2", list[1].Title);
        Assert.Equal("https://pawpatrol.fandom.com/ep2", list[1].TranscriptUrl);
    }

    [Fact]
    public void ParseTranscriptLinks_SingleFile_BlankUrl_FallsBackToInputUrl()
    {
        var text = LinksJson(Link("Single page", transcript_url: ""));
        var list = TranscriptVideoFind.ParseTranscriptLinks(WebApi(text), inputUrl: "https://scripts.example.com/only-ep");
        var c = Assert.Single(list);
        Assert.Equal("https://scripts.example.com/only-ep", c.TranscriptUrl);
    }

    [Fact]
    public void ParseTranscriptLinks_MultiEntry_BlankUrl_NoFallback_Excluded()
    {
        // 多筆時空白 transcript_url 不退用 inputUrl（只有單檔頁退用）→ 濾除，僅留好的一筆
        var text = LinksJson(Link("Blank", transcript_url: ""), Link("Good", "https://scripts.example.com/ep2"));
        var c = Assert.Single(TranscriptVideoFind.ParseTranscriptLinks(WebApi(text), inputUrl: "https://in.example.com/x"));
        Assert.Equal("Good", c.Title);
    }

    [Fact]
    public void ParseTranscriptLinks_DedupsByNormalizedUrl_PreservesFirst()
    {
        var text = LinksJson(
            Link("First", "https://scripts.example.com/ep1"),
            Link("Dup (www + trailing slash)", "https://www.scripts.example.com/ep1/"));
        var c = Assert.Single(TranscriptVideoFind.ParseTranscriptLinks(WebApi(text)));
        Assert.Equal("First", c.Title);
    }

    [Fact]
    public void ParseTranscriptLinks_MalformedUrl_Excluded()
    {
        var text = LinksJson(Link("Bad url", "not a url"), Link("Good", "https://scripts.example.com/ep2"));
        var c = Assert.Single(TranscriptVideoFind.ParseTranscriptLinks(WebApi(text)));
        Assert.Equal("Good", c.Title);
    }

    [Fact]
    public void ParseTranscriptLinks_SkipsBlankTitles()
    {
        var text = LinksJson(Link("  ", "https://scripts.example.com/blank"), Link("Keep Me", "https://scripts.example.com/keep"));
        var c = Assert.Single(TranscriptVideoFind.ParseTranscriptLinks(WebApi(text)));
        Assert.Equal("Keep Me", c.Title);
    }

    [Fact]
    public void ParseTranscriptLinks_TolerantToFencesAndProse()
    {
        var inner = LinksJson(Link("T", "https://scripts.example.com/t"));
        var text = "Here you go:\n```json\n" + inner + "\n```\nHope it helps!";
        var c = Assert.Single(TranscriptVideoFind.ParseTranscriptLinks(WebApi(text)));
        Assert.Equal("T", c.Title);
    }

    [Fact]
    public void ParseTranscriptLinks_EmptyArray_ReturnsEmpty()
        => Assert.Empty(TranscriptVideoFind.ParseTranscriptLinks(WebApi("{\"links\":[]}")));

    [Fact]
    public void ParseTranscriptLinks_NoLinksKey_ReturnsEmpty()
        => Assert.Empty(TranscriptVideoFind.ParseTranscriptLinks(WebApi("{\"other\":1}")));

    [Fact]
    public void ParseTranscriptLinks_NoMessage_ReturnsEmpty()
    {
        var json = JsonSerializer.Serialize(new { output = new object[] { new { type = "web_search_call", id = "ws_1" } } });
        Assert.Empty(TranscriptVideoFind.ParseTranscriptLinks(json));
    }

    [Fact]
    public void ParseTranscriptLinks_OutputTextConvenienceField()
    {
        var json = JsonSerializer.Serialize(new
        {
            output_text = LinksJson(Link("X", "https://scripts.example.com/x")),
            output = System.Array.Empty<object>(),
        });
        var c = Assert.Single(TranscriptVideoFind.ParseTranscriptLinks(json));
        Assert.Equal("X", c.Title);
    }

    // ── 第2階段解析：ParseOneCandidate（驗證含說話人、採傳入 transcript_url、youtube ID 抽取、容錯） ──

    [Fact]
    public void ParseOneCandidate_ParsesTitleSourceId_UsesPassedTranscriptUrl()
    {
        // 模型自報 transcript_url＝ep1，但 ParseOneCandidate 以第2階段已知（傳入）之網址為準
        var text = OneJson(Entry(title: "PAW Patrol S1E1", youtube_url: "https://youtu.be/dQw4w9WgXcQ",
            source: "PAW Patrol Wiki", transcript_url: "https://scripts.example.com/ep1"));
        var c = TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://pawpatrol.fandom.com/ep1");
        Assert.NotNull(c);
        Assert.Equal("PAW Patrol S1E1", c!.Title);
        Assert.Equal("dQw4w9WgXcQ", c.VideoId);
        Assert.Equal("PAW Patrol Wiki", c.Source);
        Assert.Equal("https://pawpatrol.fandom.com/ep1", c.TranscriptUrl); // 採傳入之 transcriptUrl，非模型自報
    }

    [Fact]
    public void ParseOneCandidate_HasSpeakerFalse_NoSample_ReturnsNull()
    {
        var text = OneJson(Entry(title: "No speakers here", has_speaker: false, sample: ""));
        Assert.Null(TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://scripts.example.com/x"));
    }

    [Fact]
    public void ParseOneCandidate_HasSpeakerTrue_NoSample_TrustsModel()
    {
        var text = OneJson(Entry(title: "Trusted", has_speaker: true, sample: ""));
        Assert.NotNull(TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://scripts.example.com/x"));
    }

    [Fact]
    public void ParseOneCandidate_SamplePresent_HeuristicOverridesModelClaim_False()
    {
        // 模型自稱 has_speaker=true，但樣本看不出說話人 → 啟發式判否 → null
        var text = OneJson(Entry(title: "Claimed but no markup", has_speaker: true,
            sample: "The pups run to the Lookout.\nAdventure Bay needs help."));
        Assert.Null(TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://scripts.example.com/x"));
    }

    [Fact]
    public void ParseOneCandidate_SamplePresent_HeuristicOverridesModelClaim_True()
    {
        // 模型自稱 has_speaker=false，但樣本看得出說話人 → 啟發式判是 → 合格
        var text = OneJson(Entry(title: "Has markup", has_speaker: false,
            sample: "Ryder: PAW Patrol!\nChase: Chase is on the case!"));
        Assert.NotNull(TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://scripts.example.com/x"));
    }

    [Fact]
    public void ParseOneCandidate_BlankTitle_ReturnsNull()
    {
        var text = OneJson(Entry(title: "  "));
        Assert.Null(TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://scripts.example.com/x"));
    }

    [Fact]
    public void ParseOneCandidate_UnparseableYoutubeUrl_VideoIdNull_TitleKept()
    {
        var text = OneJson(Entry(title: "Some Show", youtube_url: ""));
        var c = TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://scripts.example.com/show");
        Assert.NotNull(c);
        Assert.Equal("Some Show", c!.Title);
        Assert.Null(c.VideoId);        // UI 再以標題定位
    }

    [Fact]
    public void ParseOneCandidate_BareIdYoutubeUrl_Extracted()
    {
        var text = OneJson(Entry(title: "T", youtube_url: "dQw4w9WgXcQ"));
        var c = TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://scripts.example.com/t");
        Assert.Equal("dQw4w9WgXcQ", c!.VideoId);
    }

    [Fact]
    public void ParseOneCandidate_TolerantToFencesAndProse()
    {
        var inner = OneJson(Entry(title: "T", youtube_url: "dQw4w9WgXcQ"));
        var text = "Sure:\n```json\n" + inner + "\n```\nDone.";
        var c = TranscriptVideoFind.ParseOneCandidate(WebApi(text), "https://scripts.example.com/t");
        Assert.Equal("dQw4w9WgXcQ", c!.VideoId);
    }

    [Fact]
    public void ParseOneCandidate_NoMessage_ReturnsNull()
    {
        var json = JsonSerializer.Serialize(new { output = new object[] { new { type = "web_search_call", id = "ws_1" } } });
        Assert.Null(TranscriptVideoFind.ParseOneCandidate(json, "https://scripts.example.com/x"));
    }

    [Fact]
    public void ParseOneCandidate_OutputTextConvenienceField()
    {
        var json = JsonSerializer.Serialize(new
        {
            output_text = OneJson(Entry(title: "X", youtube_url: "dQw4w9WgXcQ")),
            output = System.Array.Empty<object>(),
        });
        var c = TranscriptVideoFind.ParseOneCandidate(json, "https://scripts.example.com/x");
        Assert.Equal("X", c!.Title);
    }

    // ── LooksLikeCompilation（#189 實測修：濾掉無單一乾淨逐字稿之合輯） ──

    [Theory]
    [InlineData("PAW Patrol Are All Paws on Deck to Rescue Adventure Bay! w/ Marshall, Mighty Twins & MORE | Nick Jr.", true)]
    [InlineData("PAW Patrol Full Episodes Compilation", true)]
    [InlineData("Peppa Pig - 1 Hour Compilation", true)]
    [InlineData("Best of Bluey Marathon", true)]
    [InlineData("2 Hours of Cocomelon Nursery Rhymes", true)]
    [InlineData("Pups Save a School Day", false)]
    [InlineData("Where did English come from? - Claire Bowern", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeCompilation_FlagsMultiEpisodeMixes(string? title, bool expected)
        => Assert.Equal(expected, TranscriptVideoFind.LooksLikeCompilation(title));
}
