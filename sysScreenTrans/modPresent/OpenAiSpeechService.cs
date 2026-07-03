using System.Collections.Concurrent;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScreenTrans.Present;

/// <summary>
/// OpenAI 語音合成（POST /v1/audio/speech）之 ISpeechService 實作：取回 WAV 音檔、以 SoundPlayer 播放。
/// 比 Windows 內建 SAPI 自然，且中英同一端點皆可（免另裝中文語音包）。
/// 單一背景播放序列：stopPrevious=true 清空佇列並停止當前；false 則排入接續
/// （供中英雙語自動播放「先英後中」循序不重疊）。取音或播放失敗（無金鑰／網路／格式）
/// 退回注入的 fallback（Windows SAPI），不致完全啞掉。
/// 前置條件：所有 Speak 皆由 UI 執行緒呼叫（App／ResultWindow），故產生代（_gen）之更替天然序列化。
/// </summary>
public sealed class OpenAiSpeechService : ISpeechService, IDisposable
{
    private sealed record Item(string Text, string Culture, CancellationToken Ct);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly ISpeechService? _fallback;

    private readonly BlockingCollection<Item> _queue = new();
    private readonly Task _worker;
    private CancellationTokenSource _gen = new();
    private volatile SoundPlayer? _current;
    private volatile bool _disposed;

    public OpenAiSpeechService(string apiKey, string model, string voice, int timeoutSec, ISpeechService? fallback)
    {
        _apiKey = apiKey ?? "";
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini-tts" : model;
        _voice = string.IsNullOrWhiteSpace(voice) ? "nova" : voice;
        _fallback = fallback;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec <= 0 ? 20 : timeoutSec) };
        _worker = Task.Run(ProcessLoopAsync);
    }

    public void Speak(string text, string culture, bool stopPrevious = true)
    {
        if (string.IsNullOrWhiteSpace(text) || _disposed)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _fallback?.Speak(text, culture, stopPrevious); // 無金鑰直接退回 SAPI
            return;
        }
        if (stopPrevious)
        {
            _gen.Cancel();
            _gen = new CancellationTokenSource();
            try { _current?.Stop(); } catch { /* 停止當前播放 */ }
        }
        _queue.Add(new Item(text, culture, _gen.Token));
    }

    private async Task ProcessLoopAsync()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (item.Ct.IsCancellationRequested)
            {
                continue; // 已被後續 stopPrevious 取消
            }
            try
            {
                var wav = await FetchWavAsync(item.Text, item.Ct).ConfigureAwait(false);
                if (item.Ct.IsCancellationRequested)
                {
                    continue;
                }
                FixWavSizes(wav); // OpenAI 串流 WAV 的 RIFF/data 長度為佔位值，winmm 會拒播，先修正
                var player = new SoundPlayer(new MemoryStream(wav));
                _current = player;
                player.Load();
                if (item.Ct.IsCancellationRequested)
                {
                    continue;
                }
                player.PlaySync(); // 阻塞至播畢或被 Stop()，達成佇列循序播放
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (!item.Ct.IsCancellationRequested)
                {
                    _fallback?.Speak(item.Text, item.Culture, stopPrevious: false); // 降級朗讀
                }
            }
        }
    }

    private async Task<byte[]> FetchWavAsync(string text, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var body = new { model = _model, voice = _voice, input = text, response_format = "wav" };
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 修正 OpenAI 串流 WAV 的區塊長度：RIFF 與 data 的 chunkSize 常為佔位 0xFFFFFFFF，
    /// 高階 SoundPlayer.Load() 容忍，但 winmm PlaySound（PlaySync）會判「not a valid wave file」而拒播。
    /// 依實際位元組長度回填 RIFF 與 data 大小。
    /// </summary>
    private static void FixWavSizes(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
        {
            return;
        }
        BitConverter.GetBytes((uint)(wav.Length - 8)).CopyTo(wav, 4);
        int p = 12; // 掃描各 chunk 找 data
        while (p + 8 <= wav.Length)
        {
            if (wav[p] == 'd' && wav[p + 1] == 'a' && wav[p + 2] == 't' && wav[p + 3] == 'a')
            {
                BitConverter.GetBytes((uint)(wav.Length - (p + 8))).CopyTo(wav, p + 4);
                return;
            }
            uint chunkSize = BitConverter.ToUInt32(wav, p + 4);
            if (chunkSize == 0xFFFFFFFF || chunkSize > (uint)(wav.Length - p - 8))
            {
                return; // 長度不可信，無法安全前進
            }
            p += 8 + (int)chunkSize + ((int)chunkSize & 1); // chunk 尾端補齊偶數
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _gen.Cancel();
        _queue.CompleteAdding();
        try { _current?.Stop(); } catch { /* 忽略 */ }
        _http.Dispose();
    }
}
