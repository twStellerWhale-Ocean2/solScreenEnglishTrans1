using System.Diagnostics;
using System.IO;

namespace LingoIsland.Video;

/// <summary>
/// 以本機 <c>yt-dlp</c> 取 YouTube 影片英文字幕（[techItem字幕擷取]／[modVideoCapture模組] 影片擷取契約，spec#2）：
/// <see cref="Process"/> 啟動、僅下載字幕檔（VTT）、以 <see cref="SubtitleParser"/> 解析為逐句 cue。
/// 無字幕／yt-dlp 缺失／私人或無效影片／逾時／非零離開碼一律擲 <see cref="SubtitleException"/>（中止該片、不當機）。
/// **僅取字幕文字、不下載影片內容**（<c>--skip-download</c>）。
/// </summary>
public sealed class YtDlpSubtitleFetcher : ISubtitleFetcher
{
    private readonly string _ytDlpPath;
    private readonly int _timeoutSec;

    public YtDlpSubtitleFetcher(string ytDlpPath = "yt-dlp", int timeoutSec = 60)
    {
        _ytDlpPath = ytDlpPath;
        _timeoutSec = timeoutSec;
    }

    public async Task<IReadOnlyList<SubtitleCue>> FetchAsync(string videoUrlOrId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoUrlOrId))
        {
            throw new SubtitleException("Please paste a YouTube link or video ID.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "lingoisland-subs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outTemplate = Path.Combine(dir, "sub");
            // 僅字幕、不下載影片；優先人工字幕、退自動字幕；英文（含變體 en.*）；VTT 格式；單片不展開清單。
            var args =
                $"--skip-download --write-subs --write-auto-subs --sub-langs \"en.*\" " +
                $"--sub-format vtt --no-playlist -o \"{outTemplate}\" \"{videoUrlOrId.Trim()}\"";

            var (exit, stderr) = await RunAsync(args, ct);

            // 優先取人工英文字幕（乾淨句級）；退取非自動變體；最後任一——自動字幕（en-orig/auto）為逐字滾動、句數暴增且到句暫停體驗差。
            var vtts = Directory.EnumerateFiles(dir, "*.vtt").ToList();
            var vtt = vtts.FirstOrDefault(f => f.EndsWith(".en.vtt", StringComparison.OrdinalIgnoreCase))
                   ?? vtts.FirstOrDefault(f => f.IndexOf("orig", StringComparison.OrdinalIgnoreCase) < 0
                                            && f.IndexOf("auto", StringComparison.OrdinalIgnoreCase) < 0)
                   ?? vtts.FirstOrDefault();
            if (vtt is null)
            {
                if (exit != 0)
                {
                    throw new SubtitleException("Could not fetch subtitles: " + FirstMeaningfulLine(stderr));
                }
                throw new SubtitleException("This video has no English subtitles available.");
            }

            var cues = SubtitleParser.Parse(await File.ReadAllTextAsync(vtt, ct));
            if (cues.Count == 0)
            {
                throw new SubtitleException("Subtitles were found but contained no readable lines.");
            }
            return cues;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private async Task<(int exit, string stderr)> RunAsync(string args, CancellationToken ct)
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new SubtitleException($"Fetching subtitles timed out ({_timeoutSec}s).");
            }
            var stderr = await stderrTask;
            _ = await stdoutTask;
            return (p.ExitCode, stderr);
        }
    }

    private static string FirstMeaningfulLine(string s)
    {
        var line = s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "unknown error";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
