using System.IO;
using System.Text;
using NAudio.Wave;

namespace ScreenTrans.Present;

/// <summary>
/// NAudio 麥克風錄音（[modPresent模組] 麥克風錄音契約，spec#10）：<see cref="WaveInEvent"/> 擷取
/// 16kHz 單聲道 16-bit PCM 於記憶體、放開停止後包成 WAV（<see cref="WrapWav"/> 純函式）。錄音僅存記憶體、
/// 不落地。無裝置 vs OS 隱私未授權分辨供 UI 各自降級；低於 <see cref="MinRecordMs"/> 視為誤點不送、
/// 逾 <see cref="MaxRecordMs"/> 之緩衝上限即止（軟上限，UI 另有時長守衛）。
/// </summary>
public sealed class NaudioRecorder : IAudioRecorder, IDisposable
{
    /// <summary>錄音最短時長（低於＝誤點即放、不送出）。</summary>
    public const int MinRecordMs = 300;
    /// <summary>錄音上限時長（緩衝軟上限；UI 亦有時長守衛自動停止）。</summary>
    public const int MaxRecordMs = 15000;

    private const int SampleRate = 16000;
    private const int Bits = 16;
    private const int Channels = 1;
    private const int MaxBytes = SampleRate * (Bits / 8) * Channels * MaxRecordMs / 1000;
    /// <summary>音量正規化增益：一般說話 RMS 偏低，乘此倍率使音量條有明顯高度；上限夾限至 1。</summary>
    private const double LevelGain = 4.0;

    private WaveInEvent? _wave;
    private MemoryStream? _pcm;
    private DateTime _startedUtc;
    private readonly object _lock = new();

    public bool IsRecording { get; private set; }

    public event Action<double>? LevelChanged;

    public RecordStart Start()
    {
        lock (_lock)
        {
            if (IsRecording)
            {
                return RecordStart.Ok;
            }
            if (WaveInEvent.DeviceCount == 0)
            {
                return RecordStart.NoDevice;
            }
            try
            {
                _pcm = new MemoryStream();
                _wave = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, Bits, Channels), BufferMilliseconds = 50 };
                _wave.DataAvailable += OnData;
                _wave.StartRecording();
                _startedUtc = DateTime.UtcNow;
                IsRecording = true;
                return RecordStart.Ok;
            }
            catch
            {
                CleanupDevice();
                // 裝置存在卻起不來 → 幾乎必為 OS 隱私設定封鎖（Windows 隱私權→麥克風）
                return WaveInEvent.DeviceCount > 0 ? RecordStart.PermissionDenied : RecordStart.NoDevice;
            }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_pcm is null)
            {
                return; // 已停止（放開後不再回報）
            }
            if (_pcm.Length < MaxBytes)
            {
                var take = Math.Min(e.BytesRecorded, MaxBytes - (int)_pcm.Length);
                _pcm.Write(e.Buffer, 0, take);
            }
            // 逾緩衝上限即不再追加（軟上限），但音量仍持續回報使音量條保持反應
        }
        // 於鎖外引發，避免訂閱端回呼與錄音鎖互鎖；本緩衝之音量與 _pcm 無關、可獨立計算
        LevelChanged?.Invoke(ComputeLevel(e.Buffer, e.BytesRecorded));
    }

    /// <summary>
    /// 由 16-bit 小端 PCM 緩衝算出即時音量（0–1，純函式、可單元測試、不依賴 NAudio）：RMS 正規化後乘語音
    /// 增益 <see cref="LevelGain"/> 並夾限，使一般說話音量能填出明顯高度、又不過度飽和。空／過短緩衝回 0。
    /// </summary>
    public static double ComputeLevel(byte[]? buffer, int bytes)
    {
        if (buffer is null || bytes < 2)
        {
            return 0;
        }
        var count = Math.Min(bytes, buffer.Length);
        int n = count / 2;
        if (n == 0)
        {
            return 0;
        }
        long sumSq = 0;
        for (int i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSq += (long)s * s;
        }
        double rms = Math.Sqrt((double)sumSq / n) / 32768.0;
        return Math.Clamp(rms * LevelGain, 0.0, 1.0);
    }

    public byte[]? Stop(out bool tooShort)
    {
        lock (_lock)
        {
            tooShort = false;
            if (!IsRecording)
            {
                return null;
            }
            var elapsedMs = (DateTime.UtcNow - _startedUtc).TotalMilliseconds;
            try { _wave?.StopRecording(); } catch { /* 停止失敗不致命 */ }
            var pcm = _pcm?.ToArray() ?? Array.Empty<byte>();
            CleanupDevice();
            IsRecording = false;
            if (elapsedMs < MinRecordMs || pcm.Length == 0)
            {
                tooShort = true;
                return null;
            }
            return WrapWav(pcm, SampleRate, Bits, Channels);
        }
    }

    /// <summary>把原始 PCM 包成標準 44-byte header 之 WAV（純函式、可單元測試、不依賴 NAudio）。</summary>
    public static byte[] WrapWav(byte[] pcm, int sampleRate, int bits, int channels)
    {
        int byteRate = sampleRate * channels * bits / 8;
        short blockAlign = (short)(channels * bits / 8);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);            // ChunkSize = 36 + Subchunk2Size
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                         // Subchunk1Size (PCM)
        w.Write((short)1);                   // AudioFormat = PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write((short)bits);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);                 // Subchunk2Size
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    private void CleanupDevice()
    {
        if (_wave is not null)
        {
            _wave.DataAvailable -= OnData;
            try { _wave.Dispose(); } catch { }
            _wave = null;
        }
        _pcm?.Dispose();
        _pcm = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CleanupDevice();
            IsRecording = false;
        }
    }
}
