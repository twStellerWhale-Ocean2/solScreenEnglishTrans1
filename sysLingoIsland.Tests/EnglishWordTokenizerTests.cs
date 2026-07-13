using System.Linq;
using LingoIsland.Present;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 英文句逐字切分純函式（Issue #11）：標點剝除、內部撇號/連字號與大小寫保留、
/// 切分不遺失字元（token 串接還原原句）。對應 design ＜III.D＞ intTest#11 之切分邊界。
/// </summary>
public class EnglishWordTokenizerTests
{
    private static string[] Words(string text) =>
        EnglishWordTokenizer.Tokenize(text).Where(t => t.IsWord).Select(t => t.Text).ToArray();

    [Fact]
    public void Tokenize_ConcatenatedTokens_ReconstructOriginal()
    {
        const string s = "A storm is brewing beyond the ridge. You'd best stay, traveler.";
        var joined = string.Concat(EnglishWordTokenizer.Tokenize(s).Select(t => t.Text));
        Assert.Equal(s, joined); // 切分不遺失、不竄改任何字元
    }

    [Fact]
    public void Tokenize_StripsLeadingAndTrailingPunctuation()
    {
        Assert.Equal(new[] { "world" }, Words("world."));
        Assert.Equal(new[] { "quote" }, Words("\"quote\""));
        Assert.Equal(new[] { "Hello", "there" }, Words("(Hello) there!"));
    }

    [Fact]
    public void Tokenize_KeepsInternalApostropheAndHyphen()
    {
        Assert.Equal(new[] { "it's" }, Words("it's"));
        Assert.Equal(new[] { "co-op" }, Words("co-op"));
        Assert.Equal(new[] { "You'd", "best" }, Words("You'd best"));
    }

    [Fact]
    public void Tokenize_PreservesCase()
    {
        Assert.Equal(new[] { "Storm", "RIDGE", "traveler" }, Words("Storm RIDGE traveler"));
    }

    [Fact]
    public void Tokenize_SeparatorsPreserveWhitespace()
    {
        var toks = EnglishWordTokenizer.Tokenize("a  b");
        Assert.Equal(3, toks.Count);
        Assert.True(toks[0].IsWord);
        Assert.False(toks[1].IsWord);
        Assert.Equal("  ", toks[1].Text); // 多重空白原樣保留於分隔 token
        Assert.True(toks[2].IsWord);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Tokenize_NullOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Empty(EnglishWordTokenizer.Tokenize(input));
    }

    [Fact]
    public void Tokenize_PunctuationOnly_NoWords()
    {
        Assert.Empty(Words("— ... !"));
    }
}
