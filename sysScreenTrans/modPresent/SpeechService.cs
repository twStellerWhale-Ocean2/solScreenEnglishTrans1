using System.Globalization;
using System.Speech.Synthesis;

namespace ScreenTrans.Present;

/// <summary>朗讀抽象（[techItem語音合成]）——介面化使單元測試可攔截、不實際發聲。</summary>
public interface ISpeechService
{
    /// <summary>依語言（culture，如 "en-US"／"zh-TW"）朗讀；stopPrevious 為 true 時先停前次。</summary>
    void Speak(string text, string culture, bool stopPrevious = true);
}

/// <summary>Windows 內建語音合成（SAPI，離線）之 ISpeechService 實作，依 culture 選語言。</summary>
public sealed class SpeechService : ISpeechService, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();

    public SpeechService(string? voice)
    {
        _synth.SetOutputToDefaultAudioDevice();
        // appsettings 指定語音則優先；否則各次 Speak 依 culture 自動選
        if (!string.IsNullOrWhiteSpace(voice))
        {
            try { _synth.SelectVoice(voice); }
            catch { /* 指定語音缺失，退回系統預設 */ }
        }
    }

    public void Speak(string text, string culture, bool stopPrevious = true)
    {
        if (stopPrevious)
        {
            _synth.SpeakAsyncCancelAll(); // 重複觸發先停前次
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var pb = new PromptBuilder();
            pb.StartVoice(new CultureInfo(culture));
            pb.AppendText(text);
            pb.EndVoice();
            _synth.SpeakAsync(pb);
        }
        catch
        {
            // 該語言語音缺失（如未裝中文 TTS）→ 退回預設語音直接念
            _synth.SpeakAsync(text);
        }
    }

    public void Dispose() => _synth.Dispose();

    /// <summary>列舉系統已安裝且啟用的語音名稱（供設定選單；不實際發聲）。</summary>
    public static IReadOnlyList<string> InstalledVoiceNames()
    {
        try
        {
            using var s = new SpeechSynthesizer();
            return s.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo.Name)
                .ToList();
        }
        catch
        {
            return System.Array.Empty<string>();
        }
    }
}
