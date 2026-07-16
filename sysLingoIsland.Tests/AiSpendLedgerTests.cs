using System;
using System.Collections.Generic;
using System.IO;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] AI 花費帳本（#189）：純函式（SumSince／StartOfDay／StartOfHour）與檔案往返。
/// 供 AI 動作跑前顯示本日/本小時累計花費。
/// </summary>
public class AiSpendLedgerTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lingoisland-ledger-{Guid.NewGuid():N}.json");

    private static readonly DateTimeOffset Tz = new(2026, 7, 15, 14, 30, 0, TimeSpan.FromHours(8)); // 14:30 +08

    [Fact]
    public void StartOfDay_IsLocalMidnight()
    {
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.FromHours(8)), AiSpendLedger.StartOfDay(Tz));
    }

    [Fact]
    public void StartOfHour_IsTopOfHour()
    {
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.FromHours(8)), AiSpendLedger.StartOfHour(Tz));
    }

    [Fact]
    public void SumSince_IncludesOnlyAtOrAfter()
    {
        var e = new List<(DateTimeOffset, double)>
        {
            (Tz.AddHours(-2), 1.0),   // 之前，不計
            (Tz.AddMinutes(-10), 2.0),// 本小時內
            (Tz, 3.0),                // 界線（含）
        };
        Assert.Equal(5.0, AiSpendLedger.SumSince(e, AiSpendLedger.StartOfHour(Tz)), 6); // 只計 14:00 起：2+3
    }

    [Fact]
    public void SumSince_Empty_IsZero()
    {
        Assert.Equal(0, AiSpendLedger.SumSince(new List<(DateTimeOffset, double)>(), Tz));
    }

    [Fact]
    public void Record_ThenSpentToday_AndThisHour()
    {
        var path = TempPath();
        try
        {
            var led = new AiSpendLedger(path);
            led.Record(0.10, Tz.AddHours(-3));   // 今日、非本小時
            led.Record(0.05, Tz.AddMinutes(-5)); // 今日、本小時
            led.Record(0.20, Tz.AddDays(-1));    // 昨日
            Assert.Equal(0.15, led.SpentToday(Tz), 6);   // 0.10 + 0.05
            Assert.Equal(0.05, led.SpentThisHour(Tz), 6);// 只 0.05
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Record_NonPositive_Ignored()
    {
        var path = TempPath();
        try
        {
            var led = new AiSpendLedger(path);
            led.Record(0, Tz);
            led.Record(-1, Tz);
            Assert.Equal(0, led.SpentToday(Tz));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SpentToday_MissingFile_IsZero()
    {
        Assert.Equal(0, new AiSpendLedger(TempPath()).SpentToday(Tz));
    }
}
