using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using LingoIsland;
using LingoIsland.Present;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 更新失敗分類與重試決策（#122）：純函式 <see cref="UpdateService.Classify"/>／IsRetriable／ToResult，
/// 及 <see cref="AppStatusText.UpdateFailureMessage"/> 之因→訊息映射——驗「不再一律報連線失敗」。
/// </summary>
public class UpdateServiceTests
{
    private static HttpRequestException Http(HttpStatusCode code) =>
        new("http " + (int)code, null, code);

    [Fact]
    public void Classify_Http403And429_RateLimited()
    {
        Assert.Equal(UpdateFailureKind.RateLimited, UpdateService.Classify(Http(HttpStatusCode.Forbidden)));       // 403
        Assert.Equal(UpdateFailureKind.RateLimited, UpdateService.Classify(Http((HttpStatusCode)429)));            // 429
    }

    [Fact]
    public void Classify_Http5xx_ServerError()
    {
        Assert.Equal(UpdateFailureKind.ServerError, UpdateService.Classify(Http(HttpStatusCode.BadGateway)));      // 502
        Assert.Equal(UpdateFailureKind.ServerError, UpdateService.Classify(Http(HttpStatusCode.ServiceUnavailable))); // 503
    }

    [Fact]
    public void Classify_Http404_SourceError()
    {
        Assert.Equal(UpdateFailureKind.SourceError, UpdateService.Classify(Http(HttpStatusCode.NotFound)));
    }

    [Fact]
    public void Classify_TimeoutAndCancel_Timeout()
    {
        Assert.Equal(UpdateFailureKind.Timeout, UpdateService.Classify(new TaskCanceledException()));
        Assert.Equal(UpdateFailureKind.Timeout, UpdateService.Classify(new TimeoutException()));
    }

    [Fact]
    public void Classify_SocketAndBareHttp_Offline()
    {
        Assert.Equal(UpdateFailureKind.Offline, UpdateService.Classify(new SocketException()));
        Assert.Equal(UpdateFailureKind.Offline, UpdateService.Classify(new HttpRequestException("connection lost"))); // 無 StatusCode
    }

    [Fact]
    public void Classify_JsonParse_SourceError()
    {
        Assert.Equal(UpdateFailureKind.SourceError, UpdateService.Classify(new JsonException("bad feed")));
    }

    [Fact]
    public void Classify_MessageHeuristics()
    {
        Assert.Equal(UpdateFailureKind.RateLimited, UpdateService.Classify(new Exception("API rate limit exceeded")));
        Assert.Equal(UpdateFailureKind.Offline, UpdateService.Classify(new Exception("No such host is known.")));
    }

    [Fact]
    public void Classify_WalksInnerException()
    {
        var wrapped = new Exception("update failed", new SocketException());
        Assert.Equal(UpdateFailureKind.Offline, UpdateService.Classify(wrapped));
    }

    [Fact]
    public void Classify_Unknown_WhenUnrecognized()
    {
        Assert.Equal(UpdateFailureKind.Unknown, UpdateService.Classify(new InvalidOperationException("weird")));
    }

    [Fact]
    public void IsRetriable_OnlyTransient()
    {
        Assert.True(UpdateService.IsRetriable(UpdateFailureKind.Offline));
        Assert.True(UpdateService.IsRetriable(UpdateFailureKind.Timeout));
        Assert.True(UpdateService.IsRetriable(UpdateFailureKind.ServerError));
        Assert.False(UpdateService.IsRetriable(UpdateFailureKind.RateLimited)); // 限流重試無益、不重試
        Assert.False(UpdateService.IsRetriable(UpdateFailureKind.SourceError));
        Assert.False(UpdateService.IsRetriable(UpdateFailureKind.Unknown));
    }

    [Fact]
    public void ToResult_MapsKindToResult()
    {
        Assert.Equal(UpdateCheckResult.FailedOffline, UpdateService.ToResult(UpdateFailureKind.Offline));
        Assert.Equal(UpdateCheckResult.FailedRateLimited, UpdateService.ToResult(UpdateFailureKind.RateLimited));
        Assert.Equal(UpdateCheckResult.FailedTransient, UpdateService.ToResult(UpdateFailureKind.ServerError));
        Assert.Equal(UpdateCheckResult.FailedTransient, UpdateService.ToResult(UpdateFailureKind.Timeout));
        Assert.Equal(UpdateCheckResult.FailedSource, UpdateService.ToResult(UpdateFailureKind.SourceError));
        Assert.Equal(UpdateCheckResult.FailedTransient, UpdateService.ToResult(UpdateFailureKind.Unknown));
    }

    [Fact]
    public void FailureMessage_DistinctPerCause_NotAlwaysConnection()
    {
        // 回歸防護：不同因給不同訊息，離線才提「連線」
        Assert.Equal(AppStatusText.UpdateFailedOffline, AppStatusText.UpdateFailureMessage(UpdateCheckResult.FailedOffline));
        Assert.Equal(AppStatusText.UpdateFailedRateLimited, AppStatusText.UpdateFailureMessage(UpdateCheckResult.FailedRateLimited));
        Assert.Equal(AppStatusText.UpdateFailedTransient, AppStatusText.UpdateFailureMessage(UpdateCheckResult.FailedTransient));
        Assert.Equal(AppStatusText.UpdateFailedSource, AppStatusText.UpdateFailureMessage(UpdateCheckResult.FailedSource));
        Assert.NotEqual(AppStatusText.UpdateFailedRateLimited, AppStatusText.UpdateFailedOffline);
        Assert.DoesNotContain("connection", AppStatusText.UpdateFailedRateLimited, StringComparison.OrdinalIgnoreCase);
    }
}
