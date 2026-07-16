using System.Diagnostics;
using System.Text.Json;

namespace LingoIsland.Video;

/// <summary>
/// 以本機 <c>yt-dlp</c> 依關鍵字搜尋 YouTube（[techItem字幕擷取] 延伸，#171）：
/// <c>yt-dlp "ytsearchN:關鍵字" --flat-playlist --dump-json</c> 回 NDJSON（每行一影片、含 <c>id</c>／<c>title</c>），
/// 解析為 <see cref="VideoSearchResult"/>。**只查清單、不下載影片**；免額外 API 金鑰（沿用字幕擷取之 yt-dlp）。
/// yt-dlp 缺失／逾時／失敗擲 <see cref="SubtitleException"/>（供 UI 明訊、不當機）。
/// </summary>
public sealed class YtDlpVideoSearcher : IVideoSearcher
{
    private readonly string _ytDlpPath;
    private readonly int _timeoutSec;

    public YtDlpVideoSearcher(string ytDlpPath = "yt-dlp", int timeoutSec = 30)
    {
        _ytDlpPath = ytDlpPath;
        _timeoutSec = timeoutSec;
    }

    public async Task<IReadOnlyList<VideoSearchResult>> SearchAsync(string query, int max = 8, CancellationToken ct = default, string? uploadDateToken = null)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return Array.Empty<VideoSearchResult>();
        max = Math.Clamp(max, 1, 50);

        // 清掉會破壞引號的字元；--flat-playlist：只列清單不解析每片、快
        var safe = query.Replace("\"", " ").Replace("\r", " ").Replace("\n", " ");
        string args;
        if (string.IsNullOrWhiteSpace(uploadDateToken))
        {
            args = $"\"ytsearch{max}:{safe}\" --flat-playlist --dump-json --no-warnings";
        }
        else
        {
            // 上傳日期篩選：ytsearch 不支援日期參數→改用 YouTube 搜尋結果 URL 之 sp 篩選 token（伺服器端篩），--playlist-end 限筆數
            var url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(safe) + "&sp=" + uploadDateToken;
            args = $"\"{url}\" --flat-playlist --dump-json --no-warnings --playlist-end {max}";
        }

        var (exit, stdout, stderr) = await RunAsync(args, ct);
        if (exit != 0 && stdout.Trim().Length == 0)
        {
            throw new SubtitleException("YouTube search failed: " + FirstMeaningfulLine(stderr));
        }
        return ParseResults(stdout);
    }

    /// <summary>解析 yt-dlp <c>--dump-json</c> 之 NDJSON（每行一影片）為結果清單：取 <c>id</c>／<c>title</c>；空/無 id/malformed 行略過。internal 供單元測試。</summary>
    internal static IReadOnlyList<VideoSearchResult> ParseResults(string ndjson)
    {
        var list = new List<VideoSearchResult>();
        if (string.IsNullOrWhiteSpace(ndjson)) return list;
        foreach (var raw in ndjson.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                var id = r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var title = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                int? dur = null; // yt-dlp 之 duration（秒，數字；直播/未知則缺或 0）→ null
                if (r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    && d.TryGetDouble(out var ds) && ds > 0)
                {
                    dur = (int)Math.Round(ds);
                }
                list.Add(new VideoSearchResult(id!, string.IsNullOrWhiteSpace(title) ? id!.Trim() : title!.Trim(), dur));
            }
            catch (JsonException)
            {
                // 略過非 JSON／半行輸出（如進度行）
            }
        }
        return list;
    }

    private async Task<(int exit, string stdout, string stderr)> RunAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new SubtitleException(
                "yt-dlp could not be started (ensure yt-dlp is installed and on PATH): " + ex.Message);
        }
        if (p is null)
        {
            throw new SubtitleException("yt-dlp could not be started (ensure yt-dlp is installed and on PATH).");
        }

        using (p)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec));
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                if (!ct.IsCancellationRequested)
                {
                    throw new SubtitleException($"YouTube search timed out ({_timeoutSec}s).");
                }
                throw; // 外部取消（新搜尋）→ 傳遞
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (p.ExitCode, stdout, stderr);
        }
    }

    private static string FirstMeaningfulLine(string s)
    {
        var line = s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "unknown error";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
