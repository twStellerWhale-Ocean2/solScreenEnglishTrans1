using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// [modQuery模組] 查詢契約之暫時性錯誤重試退避（Issue #7）：狀態碼分類、暫時性重試後成功、
/// 永久性不重試、重試耗盡後明確降級、次數為 0 不重試、使用者取消不重試。
/// 以注入之假 attempt／backoff 驗證，不打真網路、不真等待。
/// </summary>
public class QueryServiceRetryTests
{
    private static QueryService Svc(int maxRetries) => new("gpt-4o-mini", 15, maxRetries);

    // 記錄 backoff 呼叫的退避索引；不真的等待。
    private static (Func<int, CancellationToken, Task> backoff, List<int> calls) NoWaitBackoff()
    {
        var calls = new List<int>();
        Task Backoff(int i, CancellationToken _) { calls.Add(i); return Task.CompletedTask; }
        return (Backoff, calls);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(200, false)]
    public void IsTransientStatus_ClassifiesRetryable(int status, bool transient)
        => Assert.Equal(transient, QueryService.IsTransientStatus(status));

    [Fact]
    public async Task Transient_FailsThenSucceeds_WithinLimit_Returns()
    {
        var (backoff, calls) = NoWaitBackoff();
        var attempts = 0;
        Task<string> Attempt(CancellationToken _)
        {
            attempts++;
            if (attempts < 3) throw new TransientQueryException("429");
            return Task.FromResult("ok");
        }

        var result = await Svc(2).RunWithRetryAsync(Attempt, CancellationToken.None, backoff);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);           // 初次 + 2 次重試
        Assert.Equal(new[] { 0, 1 }, calls); // 兩次退避，索引遞增（1s、2s）
    }

    [Fact]
    public async Task Permanent_Propagates_Immediately_NoRetry()
    {
        var (backoff, calls) = NoWaitBackoff();
        var attempts = 0;
        Task<string> Attempt(CancellationToken _)
        {
            attempts++;
            throw new QueryException("401 金鑰無效");
        }

        var ex = await Assert.ThrowsAsync<QueryException>(
            () => Svc(2).RunWithRetryAsync(Attempt, CancellationToken.None, backoff));

        Assert.Contains("401", ex.Message);
        Assert.Equal(1, attempts);   // 不重試
        Assert.Empty(calls);         // 未退避
    }

    [Fact]
    public async Task Transient_Exhausted_ThrowsQueryException_AfterMaxRetries()
    {
        var (backoff, calls) = NoWaitBackoff();
        var attempts = 0;
        Task<string> Attempt(CancellationToken _)
        {
            attempts++;
            throw new TransientQueryException("503");
        }

        var ex = await Assert.ThrowsAsync<QueryException>(
            () => Svc(2).RunWithRetryAsync(Attempt, CancellationToken.None, backoff));

        Assert.Contains("after 2 retries", ex.Message);
        Assert.Equal(3, attempts);           // 初次 + 2 次重試皆失敗
        Assert.Equal(new[] { 0, 1 }, calls); // 僅在重試前退避、耗盡後不再退避
    }

    [Fact]
    public async Task MaxRetriesZero_Transient_FailsImmediately_NoBackoff()
    {
        var (backoff, calls) = NoWaitBackoff();
        var attempts = 0;
        Task<string> Attempt(CancellationToken _)
        {
            attempts++;
            throw new TransientQueryException("timeout");
        }

        await Assert.ThrowsAsync<QueryException>(
            () => Svc(0).RunWithRetryAsync(Attempt, CancellationToken.None, backoff));

        Assert.Equal(1, attempts);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task UserCancellation_Propagates_NotRetried()
    {
        var (backoff, calls) = NoWaitBackoff();
        var attempts = 0;
        Task<string> Attempt(CancellationToken _)
        {
            attempts++;
            throw new OperationCanceledException();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Svc(2).RunWithRetryAsync(Attempt, CancellationToken.None, backoff));

        Assert.Equal(1, attempts); // 取消非暫時性錯誤、不重試
        Assert.Empty(calls);
    }
}
