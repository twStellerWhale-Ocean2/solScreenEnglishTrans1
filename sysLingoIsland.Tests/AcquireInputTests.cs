using System.Linq;
using LingoIsland.Present;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 獲得頁單一輸入框之網址抽取（VideoCapturePage.ExtractUrls，epic #178 增量6′「輸入 pivot」）：
/// 由自由文字抽出 http(s) 網址、去尾標點與 markdown 收尾括號、停在 CJK；並驗證挑選規則
/// 「影片＝首個有 YouTube ID 之網址、字幕＝首個無 YouTube ID 之網址」（DoAcquireBuild 用）。
/// </summary>
public class AcquireInputTests
{
    // 挑選規則：與 DoAcquireBuild 一致（影片＝首個可取 ID 者；字幕＝首個不可取 ID 者）
    private static string? PickVideoId(System.Collections.Generic.IReadOnlyList<string> urls)
        => urls.Select(VideoCapturePage.ExtractVideoId).FirstOrDefault(v => v is not null);
    private static string? PickSubtitle(System.Collections.Generic.IReadOnlyList<string> urls)
        => urls.FirstOrDefault(u => VideoCapturePage.ExtractVideoId(u) is null);

    [Fact]
    public void UserExample_PlainLines_PicksVideoAndSubtitle()
    {
        // 使用者實際貼法：標題 + 影片：URL + 字幕：URL（各自成行、含中文標籤）
        var text =
            "Pups and the Pirate Treasure\n\n" +
            "影片：\nhttps://www.youtube.com/watch?v=hc4hH7JMV_g\n\n" +
            "字幕：\nhttps://pawpatrol.fandom.com/wiki/Pups_and_the_Pirate_Treasure/Transcript\n";
        var urls = VideoCapturePage.ExtractUrls(text);
        Assert.Equal(2, urls.Count);
        Assert.Equal("hc4hH7JMV_g", PickVideoId(urls));
        Assert.Equal("https://pawpatrol.fandom.com/wiki/Pups_and_the_Pirate_Treasure/Transcript", PickSubtitle(urls));
    }

    [Fact]
    public void MarkdownLinks_WithUtm_StripTrailingParenKeepQuery()
    {
        // ChatGPT 常給 markdown 連結：[標題](url)＋utm；URL 尾之 ) 應去除、utm 保留（不影響 ExtractVideoId）
        var text =
            "影片：[Pups｜PAW Patrol](https://www.youtube.com/watch?v=hc4hH7JMV_g&utm_source=chatgpt.com)\n" +
            "字幕：[Transcript](https://pawpatrol.fandom.com/wiki/Pups_and_the_Pirate_Treasure/Transcript)\n";
        var urls = VideoCapturePage.ExtractUrls(text);
        Assert.Equal(2, urls.Count);
        Assert.DoesNotContain(urls, u => u.EndsWith(")"));
        Assert.Equal("hc4hH7JMV_g", PickVideoId(urls));
        Assert.Equal("https://pawpatrol.fandom.com/wiki/Pups_and_the_Pirate_Treasure/Transcript", PickSubtitle(urls));
    }

    [Fact]
    public void WikiUrlWithBalancedParens_Kept()
    {
        // 維基式網址內含配對括號 (…)：URL 尾之 ) 有配對 ( → 不得誤刪
        var urls = VideoCapturePage.ExtractUrls("字幕 https://en.wikipedia.org/wiki/Mercury_(element)");
        Assert.Single(urls);
        Assert.Equal("https://en.wikipedia.org/wiki/Mercury_(element)", urls[0]);
    }

    [Fact]
    public void CjkAndPunctuation_TerminateAndTrim()
    {
        // URL 應停在 CJK（「，」），尾隨半形句點去除；不把中文說明黏進網址
        var urls = VideoCapturePage.ExtractUrls("see https://example.com/page. and https://example.org/x，說明");
        Assert.Equal(new[] { "https://example.com/page", "https://example.org/x" }, urls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no links here, just text")]
    [InlineData("ftp://example.com/file")] // 僅認 http(s)
    public void NoHttpUrls_Empty(string? text)
        => Assert.Empty(VideoCapturePage.ExtractUrls(text));

    [Fact]
    public void OnlyVideoUrl_SubtitlePickIsNull()
    {
        // 只給影片網址 → 有 id 但字幕挑選為 null（DoAcquireBuild 據此回報缺字幕、不動作）
        var urls = VideoCapturePage.ExtractUrls("https://youtu.be/hc4hH7JMV_g");
        Assert.Equal("hc4hH7JMV_g", PickVideoId(urls));
        Assert.Null(PickSubtitle(urls));
    }

    [Fact]
    public void OnlySubtitleUrl_VideoPickIsNull()
    {
        // 只給字幕網址 → 影片挑選為 null（DoAcquireBuild 據此回報缺影片、不動作）
        var urls = VideoCapturePage.ExtractUrls("https://pawpatrol.fandom.com/wiki/Foo/Transcript");
        Assert.Null(PickVideoId(urls));
        Assert.Equal("https://pawpatrol.fandom.com/wiki/Foo/Transcript", PickSubtitle(urls));
    }
}
