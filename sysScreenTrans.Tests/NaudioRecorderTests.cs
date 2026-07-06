using System;
using System.Text;
using ScreenTrans.Present;
using Xunit;

namespace ScreenTrans.Tests;

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
}
