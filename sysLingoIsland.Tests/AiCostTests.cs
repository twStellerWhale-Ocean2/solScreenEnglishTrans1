using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>AI 費用估算（<see cref="AiCost"/>，AI 動作對話視窗）：已知模型估 token 費、web_search 加工具費、未知模型回 null、模型比對不分大小寫。</summary>
public class AiCostTests
{
    [Fact]
    public void EstimateUsd_KnownModel_TokenCost()
    {
        // gpt-4o-mini：in 0.15 / out 0.60 per 1M → 1M in + 1M out = 0.15 + 0.60 = 0.75
        var usd = AiCost.EstimateUsd("gpt-4o-mini", 1_000_000, 1_000_000);
        Assert.NotNull(usd);
        Assert.Equal(0.75, usd!.Value, 6);
    }

    [Fact]
    public void EstimateUsd_WebSearch_AddsToolFee()
    {
        var noWeb = AiCost.EstimateUsd("gpt-4.1-mini", 0, 0, webSearch: false);
        var web = AiCost.EstimateUsd("gpt-4.1-mini", 0, 0, webSearch: true);
        Assert.Equal(0.0, noWeb!.Value, 6);
        Assert.Equal(AiCost.WebSearchCallUsd, web!.Value, 6); // 純工具費（0 tokens）
    }

    [Fact]
    public void EstimateUsd_UnknownModel_Null()
    {
        Assert.Null(AiCost.EstimateUsd("no-such-model", 1000, 1000));
        Assert.False(AiCost.HasRate("no-such-model"));
        Assert.True(AiCost.HasRate("gpt-4o-mini"));
    }

    [Fact]
    public void EstimateUsd_CaseInsensitiveModel()
    {
        Assert.NotNull(AiCost.EstimateUsd("GPT-4O-MINI", 1000, 1000));
    }
}
