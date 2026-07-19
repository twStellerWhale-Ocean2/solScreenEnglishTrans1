namespace LingoIsland.Video;

// 影片 AI 管線之共用型別（[modVideoCapture模組]）：token 用量記帳 SpeakerUsage 與可讀失敗 SpeakerEnrichException。
// 原隨已移除之說話人補全介面（epic #178 增量6′：砍 finder／結果表）而生，因仍為字幕整理／對齊
// （OpenAiTranscriptAligner／TranscriptFetch 等）共用而保留，於此抽出獨立成檔。

/// <summary>某次 AI 呼叫之 token 用量＋模型＋是否含 web_search（供費用估算，AI 動作對話視窗）。</summary>
public sealed record SpeakerUsage(int InputTokens, int OutputTokens, int TotalTokens, string Model = "", bool WebSearch = false);

/// <summary>影片 AI 動作之明確可讀失敗（無金鑰、網路錯、逾時、回應無法解析等）——中止該次動作、不當機不無聲失敗。</summary>
public sealed class SpeakerEnrichException : Exception
{
    public SpeakerEnrichException(string message) : base(message) { }
}
