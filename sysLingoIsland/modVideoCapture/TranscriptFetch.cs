using System.Diagnostics;
using System.Text;

namespace LingoIsland.Video;

/// <summary>
/// 取字幕檔網址之原始內容（[modVideoCapture模組]，epic #178 增量5′）：以 <c>curl</c> 子行程 GET 使用者提供之字幕檔／逐字稿 URL，
/// 回原始文字（HTML 或純文字；標準格式交 <see cref="SubtitleParser"/> 免費解析，網頁式無時間戳者才交 <see cref="TranscriptAlign.StripToPlainText"/> 去雜訊、再由 <see cref="ITranscriptAligner.ExtractTimedCuesAsync"/> AI 抽取）。
/// <b>為何用 curl 而非 <c>HttpClient</c></b>：Fandom／Cloudflare 等以 TLS/HTTP 指紋辨識，<c>HttpClient</c> 即使帶完整瀏覽器標頭仍回 403（增量5′ smoke 實測）；
/// curl 之指紋獲放行。curl.exe 隨 Windows 10 1803+ 內建（本 app 目標 ≥ 19041），為本 app 唯一之外部行程依賴（epic #178 增量6′-B 廢止 Whisper 後，已不再依賴 yt-dlp／ffmpeg）。純 IO、不列單元測試；失敗擲 <see cref="SpeakerEnrichException"/>（人類可讀）、取消傳遞。
/// </summary>
public static class TranscriptFetch
{
    private const int TimeoutSec = 60;

    public static async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new SpeakerEnrichException("此影片沒有可讀取的字幕檔網址。");
        }
        var u = url.Trim();
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new SpeakerEnrichException("字幕檔網址必須以 http:// 或 https:// 開頭。");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "curl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        // ArgumentList（非 shell 字串，免注入）：-sSLf＝靜默/顯錯/跟隨轉址/HTTP 錯以離開碼反映；--compressed 收 gzip；瀏覽器標頭繞過 bot 防護（實測 Fandom 403→200）。
        foreach (var a in new[]
        {
            "-sSLf", "--max-time", TimeoutSec.ToString(), "--compressed",
            "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            "-H", "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            "-H", "Accept-Language: en-US,en;q=0.9",
            "-H", "Sec-Fetch-Mode: navigate",
            "-H", "Sec-Fetch-Site: none",
            u,
        })
        {
            psi.ArgumentList.Add(a);
        }

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            throw new SpeakerEnrichException("無法啟動 curl 讀取字幕檔網址（curl 隨 Windows 10 1803+ 內建，請確認已加入 PATH）：" + ex.Message);
        }
        if (p is null) { throw new SpeakerEnrichException("無法啟動 curl 讀取字幕檔網址。"); }

        using (p)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSec + 15));
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                if (ct.IsCancellationRequested) { throw; }
                throw new SpeakerEnrichException($"讀取字幕檔網址逾時（{TimeoutSec} 秒）。");
            }
            var body = await stdoutTask;
            var err = await stderrTask;
            if (p.ExitCode != 0)
            {
                throw new SpeakerEnrichException($"無法讀取字幕檔網址（curl 離開碼 {p.ExitCode}）——請確認網址可連線：" + FirstMeaningfulLine(err));
            }
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new SpeakerEnrichException("字幕檔網址未回傳任何內容。");
            }
            return body;
        }
    }

    private static string FirstMeaningfulLine(string s)
    {
        var line = s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "未知錯誤";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
