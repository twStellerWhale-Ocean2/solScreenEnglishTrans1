namespace LingoIsland.Video;

// 字幕取得／解析之可讀失敗（[techItem字幕擷取]，spec#2）：原隨已移除之 ISubtitleFetcher 介面而生，
// 因仍由字幕檔解析（SubtitleYaml）等字幕主線共用、並由 VideoCapturePage 逐列容錯而保留，於此抽出獨立成檔。

/// <summary>字幕取得／解析之明確可讀失敗（無字幕、YAML 格式錯、私人／無效影片、逾時等）——中止該片、不當機不無聲失敗。</summary>
public sealed class SubtitleException : Exception
{
    public SubtitleException(string message) : base(message) { }
}
