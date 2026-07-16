using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LingoIsland.Video;

/// <summary>
/// AI 花費帳本（[modVideoCapture模組]，#189）：本機記錄每次 AI 呼叫實際估算花費（USD＋時間戳），存
/// <c>%APPDATA%\LingoIsland\ai-spend-ledger.json</c>，供各 AI 動作「跑前」顯示**本日／本小時累計花費**（使用者掌握用量）。
/// 注意：這是**本 app 自己的記帳**，非 OpenAI 帳戶真實餘額（後者無法以一般 API 金鑰讀取）。讀寫失敗一律靜默降級、不影響主流程。
/// 純函式（<see cref="SumSince"/>／<see cref="StartOfDay"/>／<see cref="StartOfHour"/>）拆出供單元測試。
/// </summary>
public sealed class AiSpendLedger
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;

    /// <summary>保留天數：超過即於寫入時修剪，避免帳本無界成長（本日/本小時查詢只需近期）。</summary>
    private const int RetentionDays = 45;

    public AiSpendLedger(string? path = null) => _path = path ?? DefaultPath;

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoIsland");

    public static string DefaultPath => Path.Combine(DefaultDir, "ai-spend-ledger.json");

    /// <summary>一筆花費：時間（ISO）＋估算美元。</summary>
    public sealed class Entry
    {
        [JsonPropertyName("at")] public string At { get; set; } = "";
        [JsonPropertyName("usd")] public double Usd { get; set; }
    }

    /// <summary>記一次花費（<paramref name="usd"/>≤0 忽略）；順帶修剪逾 <see cref="RetentionDays"/> 天者。寫入失敗靜默降級。</summary>
    public void Record(double usd, DateTimeOffset now)
    {
        if (usd <= 0) { return; }
        var list = LoadRaw();
        list.Add(new Entry { At = now.ToString("o"), Usd = usd });
        var cutoff = now.AddDays(-RetentionDays);
        var pruned = list.Where(e => !(DateTimeOffset.TryParse(e.At, out var t) && t < cutoff)).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(pruned, Opts));
        }
        catch { /* 不致命 */ }
    }

    /// <summary>本日（自當地午夜起）累計估算花費（USD）。</summary>
    public double SpentToday(DateTimeOffset now) => SpentSince(StartOfDay(now));

    /// <summary>本小時（自當地整點起）累計估算花費（USD）。</summary>
    public double SpentThisHour(DateTimeOffset now) => SpentSince(StartOfHour(now));

    private double SpentSince(DateTimeOffset since) =>
        SumSince(LoadRaw().Select(e => (DateTimeOffset.TryParse(e.At, out var t) ? t : DateTimeOffset.MinValue, e.Usd)), since);

    private List<Entry> LoadRaw()
    {
        try { return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? new(); }
        catch { return new(); }
    }

    // ── 純函式（internal 供單元測試）──

    /// <summary>當地日之起點（午夜，同 offset）。</summary>
    internal static DateTimeOffset StartOfDay(DateTimeOffset t) => new(t.Year, t.Month, t.Day, 0, 0, 0, t.Offset);

    /// <summary>當地小時之起點（整點，同 offset）。</summary>
    internal static DateTimeOffset StartOfHour(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset);

    /// <summary>純函式：累加 <paramref name="since"/>（含）以後之花費。</summary>
    internal static double SumSince(IEnumerable<(DateTimeOffset At, double Usd)> entries, DateTimeOffset since) =>
        entries.Where(e => e.At >= since).Sum(e => e.Usd);
}
