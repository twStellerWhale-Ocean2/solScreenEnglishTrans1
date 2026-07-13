using System;
using System.Text;
using LingoIsland.Present;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// WAV 封裝純函式（[modPresent模組] 麥克風錄音契約，spec#10）：44-byte RIFF/WAVE header 正確、
/// 大小欄位與格式欄位正確；可測、不需麥克風裝置。
/// </summary>
public class NaudioRecorderTests
{
    [Fact]
    public void WrapWav_HasRiffWaveHeader_AndCorrectSizes()
    {
        var pcm = new byte[100];
        var wav = NaudioRecorder.WrapWav(pcm, 16000, 16, 1);

        Assert.Equal(44 + 100, wav.Length);                        // 44-byte header + data
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(36 + 100, BitConverter.ToInt32(wav, 4));      // ChunkSize = 36 + dataLen
        Assert.Equal(100, BitConverter.ToInt32(wav, 40));          // Subchunk2Size = dataLen
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 20));     // AudioFormat = PCM
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 22));     // NumChannels
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));        // SampleRate
        Assert.Equal((short)16, BitConverter.ToInt16(wav, 34));    // BitsPerSample
    }

    [Fact]
    public void WrapWav_EmptyPcm_ValidHeaderNoData()
    {
        var wav = NaudioRecorder.WrapWav(Array.Empty<byte>(), 16000, 16, 1);
        Assert.Equal(44, wav.Length);
        Assert.Equal(0, BitConverter.ToInt32(wav, 40));            // data length 0
    }

    [Fact]
    public void WrapWav_ByteRateAndBlockAlign_ForStereo()
    {
        var wav = NaudioRecorder.WrapWav(new byte[8], 44100, 16, 2);
        Assert.Equal(44100 * 2 * 16 / 8, BitConverter.ToInt32(wav, 28)); // ByteRate
        Assert.Equal((short)(2 * 16 / 8), BitConverter.ToInt16(wav, 32)); // BlockAlign
    }

    // ---- 即時音量純函式（RMS→0–1，成績框藍色音量條，spec#10）：不需麥克風裝置 ----

    [Fact]
    public void ComputeLevel_Silence_IsZero()
    {
        var buf = new byte[320]; // 全零 PCM ＝ 靜音
        Assert.Equal(0, NaudioRecorder.ComputeLevel(buf, buf.Length));
    }

    [Fact]
    public void ComputeLevel_EmptyOrTinyBuffer_IsZero()
    {
        Assert.Equal(0, NaudioRecorder.ComputeLevel(Array.Empty<byte>(), 0));
        Assert.Equal(0, NaudioRecorder.ComputeLevel(null, 0));
        Assert.Equal(0, NaudioRecorder.ComputeLevel(new byte[1], 1)); // 不足一個 16-bit 取樣
    }

    [Fact]
    public void ComputeLevel_LouderInput_YieldsHigherLevel_AndStaysInRange()
    {
        var quiet = Pcm16(1000, 160);
        var loud = Pcm16(8000, 160);
        var lq = NaudioRecorder.ComputeLevel(quiet, quiet.Length);
        var ll = NaudioRecorder.ComputeLevel(loud, loud.Length);
        Assert.True(ll > lq);
        Assert.InRange(lq, 0, 1);
        Assert.InRange(ll, 0, 1);
    }

    [Fact]
    public void ComputeLevel_FullScale_ClampsToOne()
    {
        var max = Pcm16(short.MaxValue, 160); // RMS≈滿刻度 → ×增益後夾限至 1
        Assert.Equal(1.0, NaudioRecorder.ComputeLevel(max, max.Length));
    }

    [Fact]
    public void ComputeLevel_HonorsByteCount_IgnoresTail()
    {
        var buf = Pcm16(8000, 160);              // 前段高振幅
        Assert.Equal(0, NaudioRecorder.ComputeLevel(new byte[buf.Length], 0)); // bytes=0 → 0
        Assert.True(NaudioRecorder.ComputeLevel(buf, 4) > 0); // 只算前兩個取樣仍有音量
    }

    [Fact]
    public void HasAudibleInput_SilenceOrTinyNoise_IsFalse()
    {
        Assert.False(NaudioRecorder.HasAudibleInput(new byte[320], 320));
        Assert.False(NaudioRecorder.HasAudibleInput(Pcm16(20, 160), 320));
    }

    [Fact]
    public void HasAudibleInput_SpeechLikeAmplitude_IsTrue()
    {
        var pcm = Pcm16(3000, 160);
        Assert.True(NaudioRecorder.HasAudibleInput(pcm, pcm.Length));
    }

    // 產生固定振幅之 16-bit 小端 PCM（samples 個取樣）
    private static byte[] Pcm16(short amp, int samples)
    {
        var b = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            b[i * 2] = (byte)(amp & 0xFF);
            b[i * 2 + 1] = (byte)((amp >> 8) & 0xFF);
        }
        return b;
    }
}
