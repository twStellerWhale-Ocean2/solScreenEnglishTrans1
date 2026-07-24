using System.Collections.Generic;
using LingoIsland.Query;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 匯入字幕自動屏蔽（#217）：<see cref="ThemeStore.ParseBlockedWords"/>（全半形逗號／去空白／大小寫不敏感去重）
/// 與 <see cref="SubtitleBlocklist.Remove"/>（純字串比對移除、收合空白、整句剔除、不動說話人與時間、無表原樣）。
/// </summary>
public class SubtitleBlocklistTests
{
    [Fact]
    public void ParseBlockedWords_SplitsHalfAndFullWidthComma_TrimsDedupes()
    {
        var words = ThemeStore.ParseBlockedWords(" (SNORT) ,， (LAUGHS)，(snort), ");
        Assert.Equal(new[] { "(SNORT)", "(LAUGHS)" }, words); // 全半形逗號皆分隔、去空白、大小寫不敏感去重
        Assert.Empty(ThemeStore.ParseBlockedWords(null));
        Assert.Empty(ThemeStore.ParseBlockedWords("   "));
    }

    [Fact]
    public void Remove_StripsBlockedCaseInsensitive_CollapsesWhitespace()
    {
        var cues = new List<SubtitleCue> { new("(Snort) I'm Peppa Pig. (SNORT)", 1.0, "Peppa") };
        var got = SubtitleBlocklist.Remove(cues, new[] { "(SNORT)" });
        var cue = Assert.Single(got);
        Assert.Equal("I'm Peppa Pig.", cue.Text);   // 大小寫不敏感、移除後收合空白
        Assert.Equal("Peppa", cue.Speaker);          // 說話人不動
        Assert.Equal(1.0, cue.StartSec);             // 時間不動
    }

    [Fact]
    public void Remove_DropsCueThatBecomesEmpty()
    {
        var cues = new List<SubtitleCue> { new("(SNORT)", 1.0), new("Hello", 2.0) };
        var got = SubtitleBlocklist.Remove(cues, new[] { "(SNORT)" });
        Assert.Equal(new[] { "Hello" }, System.Linq.Enumerable.Select(got, c => c.Text)); // 整句只剩屏蔽字串→剔除
    }

    [Fact]
    public void Remove_NoBlockedWords_ReturnsSameList_NoRegexInterpretation()
    {
        var cues = new List<SubtitleCue> { new("a (b) c", 1.0) };
        Assert.Same(cues, SubtitleBlocklist.Remove(cues, null));                       // 無表原樣返回
        Assert.Same(cues, SubtitleBlocklist.Remove(cues, System.Array.Empty<string>()));
        var got = SubtitleBlocklist.Remove(cues, new[] { "(x)" });                     // 括號屬字面、非 regex——不命中即不動
        Assert.Equal("a (b) c", Assert.Single(got).Text);
    }
}
