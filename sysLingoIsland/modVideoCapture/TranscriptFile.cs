using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LingoIsland.Video;

/// <summary>
/// 讀取本機自製字幕檔（[modVideoCapture模組]，#193）：與 <see cref="TranscriptFetch"/> **責任對稱**之純 IO 取得端——
/// 後者以 curl 取網址原文、本類讀本機檔原文；兩者取得之原文交**同一套** <see cref="SubtitleParser"/> 免費解析（解析與來源無關）。
/// <b>唯讀存取</b>：不修改、不回寫、不移動使用者原檔。<b>不做快取</b>（<see cref="SubtitleStore"/> 已負責）、<b>不掃描目錄</b>、
/// <b>不做格式偵測</b>——能否解析由 <see cref="SubtitleParser"/> 決定，<see cref="AllowedExtensions"/> 僅供檔案對話框篩選與拖放初篩
/// （故本機 fandom 式 <c>HH:MM:SS</c> 純文字逐字稿走 <c>.txt</c> 亦受支援，不因白名單自我閹割）。
/// 失敗擲 <see cref="SubtitleException"/>（人類可讀）。純函式部分（<see cref="Decode"/>／<see cref="LooksMisdecoded"/>／
/// <see cref="HeaderOf"/>）internal 供單元測試；檔案 IO 僅 smoke。
/// </summary>
public static class TranscriptFile
{
    /// <summary>單檔大小上限（design ＜III.B.(A)＞ 定值）：逾限明訊拒收、不默默截斷。</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>單批檔數上限（design ＜III.B.(A)＞ 定值）：逾限明訊拒收、不默默截斷。</summary>
    public const int MaxBatchFiles = 50;

    /// <summary>檔頭區之保底行數：全檔無時間行時（如尚未加時間碼之逐字稿）僅掃前 N 行，免把台詞內連結誤採為影片網址。</summary>
    internal const int HeaderFallbackLines = 20;

    /// <summary>疑似編碼不符之判準（design ＜III.B.(A)＞ 定值）：U+FFFD 佔比門檻。</summary>
    internal const double MisdecodeRatio = 0.01;

    /// <summary>疑似編碼不符之判準（design ＜III.B.(A)＞ 定值）：U+FFFD 連續出現門檻。</summary>
    internal const int MisdecodeRun = 20;

    /// <summary>副檔名白名單——**僅用於檔案對話框篩選與拖放初篩，非格式判斷**。</summary>
    public static readonly string[] AllowedExtensions = { ".srt", ".vtt", ".txt" };

    /// <summary><c>OpenFileDialog</c> 之 Filter（與 <see cref="AllowedExtensions"/> 同源，免兩處各寫一份）。</summary>
    public const string DialogFilter = "字幕檔 (*.srt;*.vtt;*.txt)|*.srt;*.vtt;*.txt|所有檔案 (*.*)|*.*";

    // 時間行：VTT/SRT 之「-->」箭頭，或 fandom 式行首 HH:MM:SS —— 兩者皆代表「檔頭註解區已結束」。
    private static readonly Regex TimeLine = new(@"-->|^\s*\d{1,2}:\d{2}:\d{2}", RegexOptions.Compiled);

    /// <summary>副檔名是否在白名單內（大小寫不敏感）；供對話框篩選與拖放初篩，**非**能否解析之判準。</summary>
    public static bool HasAllowedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        var ext = Path.GetExtension(path);
        return AllowedExtensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 讀取本機字幕檔全文（唯讀）。不存在／無權限／被佔用／過大／空檔皆擲 <see cref="SubtitleException"/>（人類可讀、可直接顯示給使用者）。
    /// 編碼依 <see cref="Decode"/>（BOM 優先、無 BOM 以 UTF-8）；解讀疑似不符**不擲例外**（見 <see cref="LooksMisdecoded"/>），
    /// 交由呼叫端標示狀態、使用者自摘要／彙總表一眼看出亂碼而取消。
    /// </summary>
    public static string Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { throw new SubtitleException("沒有指定字幕檔。"); }
        var name = SafeName(path);

        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception ex) { throw new SubtitleException($"無法讀取字幕檔 {name}：{ex.Message}"); }

        if (!info.Exists) { throw new SubtitleException($"找不到字幕檔 {name}——請重新選檔或改貼網址。"); }
        if (info.Length > MaxBytes)
        {
            throw new SubtitleException($"字幕檔 {name} 過大（{info.Length / 1024.0 / 1024.0:0.0} MB，上限 {MaxBytes / 1024 / 1024} MB）。");
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }                                      // 唯讀開檔：不修改、不回寫原檔
        catch (UnauthorizedAccessException) { throw new SubtitleException($"沒有權限讀取字幕檔 {name}。"); }
        catch (FileNotFoundException) { throw new SubtitleException($"找不到字幕檔 {name}——請重新選檔或改貼網址。"); }
        catch (DirectoryNotFoundException) { throw new SubtitleException($"找不到字幕檔 {name} 所在的資料夾——請重新選檔或改貼網址。"); }
        catch (IOException ex) { throw new SubtitleException($"字幕檔 {name} 正被其他程式使用或無法讀取：{ex.Message}"); }

        var text = Decode(bytes);
        if (string.IsNullOrWhiteSpace(text)) { throw new SubtitleException($"字幕檔 {name} 是空的。"); }
        return text;
    }

    /// <summary>
    /// **只讀檔頭區**（#193 選檔預掃描專用）：逐行串流讀至第一個時間行即止（或 <see cref="HeaderFallbackLines"/> 行封頂），
    /// 供取檔內自帶之影片網址。相對 <see cref="Read"/> 之全檔讀取，本法對整季數十檔之預掃描代價極低，
    /// 且**結構性保證不會在選檔當下解析全檔**（設計 ＜III.B.(A)＞ 之責任邊界）。失敗擲 <see cref="SubtitleException"/>。
    /// </summary>
    public static string ReadHeader(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { throw new SubtitleException("沒有指定字幕檔。"); }
        var name = SafeName(path);
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sb = new StringBuilder();
            for (var i = 0; i < HeaderFallbackLines; i++)
            {
                var line = reader.ReadLine();
                if (line is null) { break; }
                if (TimeLine.IsMatch(line)) { break; }   // 進入時間軸＝檔頭註解區已結束
                sb.AppendLine(line);
            }
            return sb.ToString();
        }
        catch (UnauthorizedAccessException) { throw new SubtitleException($"沒有權限讀取字幕檔 {name}。"); }
        catch (FileNotFoundException) { throw new SubtitleException($"找不到字幕檔 {name}——請重新選檔或改貼網址。"); }
        catch (DirectoryNotFoundException) { throw new SubtitleException($"找不到字幕檔 {name} 所在的資料夾——請重新選檔或改貼網址。"); }
        catch (IOException ex) { throw new SubtitleException($"字幕檔 {name} 正被其他程式使用或無法讀取：{ex.Message}"); }
    }

    // 時間戳：VTT/SRT 之 HH:MM:SS,mmm（毫秒可省）或 fandom 式 HH:MM:SS。取「秒」為單位之三段式。
    private static readonly Regex Stamp = new(@"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})(?:[.,](?<ms>\d{1,3}))?", RegexOptions.Compiled);

    /// <summary>
    /// 掃描字幕檔之**最後一個時間戳**（秒），供選檔當下即顯示長度（#193 USR 回饋）。
    /// <b>刻意不做全檔解析</b>——逐行串流、只記最後一個時間戳，不建構 cue 物件、不抽說話人、不排序；
    /// 代價遠低於 <see cref="SubtitleParser"/> 之解析，故不違反「選檔預掃描不阻塞 UI」之責任邊界（仍應於背景執行緒呼叫）。
    /// 無任何時間戳（如尚未加時間碼之逐字稿）回 null。失敗擲 <see cref="SubtitleException"/>。
    /// </summary>
    public static double? ScanLastTimestampSec(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { throw new SubtitleException("沒有指定字幕檔。"); }
        var name = SafeName(path);
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            double? last = null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var sec = LastStampOf(line);
                if (sec.HasValue && (!last.HasValue || sec.Value > last.Value)) { last = sec; }
            }
            return last;
        }
        catch (UnauthorizedAccessException) { throw new SubtitleException($"沒有權限讀取字幕檔 {name}。"); }
        catch (FileNotFoundException) { throw new SubtitleException($"找不到字幕檔 {name}——請重新選檔或改貼網址。"); }
        catch (DirectoryNotFoundException) { throw new SubtitleException($"找不到字幕檔 {name} 所在的資料夾——請重新選檔或改貼網址。"); }
        catch (IOException ex) { throw new SubtitleException($"字幕檔 {name} 正被其他程式使用或無法讀取：{ex.Message}"); }
    }

    /// <summary>單行中最大的時間戳（秒；純函式）。一行可能含起訖兩個時間（`a --&gt; b`），取較大者＝該句訖點。無時間戳回 null。</summary>
    internal static double? LastStampOf(string? line)
    {
        if (string.IsNullOrEmpty(line)) { return null; }
        double? best = null;
        foreach (Match m in Stamp.Matches(line))
        {
            var sec = int.Parse(m.Groups["h"].Value) * 3600
                    + int.Parse(m.Groups["m"].Value) * 60
                    + int.Parse(m.Groups["s"].Value)
                    + (m.Groups["ms"].Success ? int.Parse(m.Groups["ms"].Value.PadRight(3, '0')) / 1000.0 : 0);
            if (!best.HasValue || sec > best.Value) { best = sec; }
        }
        return best;
    }

    /// <summary>檔名（不含路徑）——UI 與錯誤訊息一律只顯檔名，完整路徑僅存內部／tooltip（絕對路徑含使用者名稱與資料夾結構，防截圖外洩）。</summary>
    public static string SafeName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return "（未命名）"; }
        try { return Path.GetFileName(path); }
        catch { return path; }
    }

    /// <summary>
    /// 位元組→文字（純函式）：**BOM 優先**（UTF-8／UTF-16 LE·BE／UTF-32 LE·BE），無 BOM 一律以 UTF-8 解讀。
    /// UTF-8 解到無效位元組時 .NET 預設以 U+FFFD 取代（不擲例外）——此為 <see cref="LooksMisdecoded"/> 之判準來源。
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) { return ""; }
        // UTF-32 之 BOM 與 UTF-16 前兩位元組相同，須先判 4 位元組者。
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetString(bytes, 4, bytes.Length - 4);
        }
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(bytes, 4, bytes.Length - 4);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 是否疑似編碼不符（純函式，design ＜III.B.(A)＞ 定值）：以 UTF-8 解 Big5／ANSI 位元組**通常不擲例外、只產生 U+FFFD 替代字元**，
    /// 故以替代字元「佔比 ≥ <see cref="MisdecodeRatio"/>」**或**「連續出現 ≥ <see cref="MisdecodeRun"/> 個」為可驗證判準。
    /// 判為疑似不符**不中止**——由呼叫端標示狀態，使用者仍可自摘要一眼看出亂碼而取消。
    /// </summary>
    internal static bool LooksMisdecoded(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }
        var total = 0;
        var run = 0;
        var maxRun = 0;
        foreach (var c in text)
        {
            if (c == '�')
            {
                total++;
                run++;
                if (run > maxRun) { maxRun = run; }
            }
            else { run = 0; }
        }
        if (total == 0) { return false; }
        return maxRun >= MisdecodeRun || (double)total / text.Length >= MisdecodeRatio;
    }

    /// <summary>
    /// 檔頭區（純函式，#193）＝**第一個時間行之前**（`-->` 箭頭或行首 <c>HH:MM:SS</c>）。供自檔內取影片網址時**限縮掃描範圍**——
    /// 台詞內的推廣連結、字幕組署名、逐字稿頁「相關影片」不得被誤採為主角，且與「寫在檔頭註解」之使用者心智模型一致。
    /// 全檔無時間行時（如尚未加時間碼之逐字稿）退為前 <see cref="HeaderFallbackLines"/> 行，仍不掃全檔。
    /// </summary>
    internal static string HeaderOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var take = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (TimeLine.IsMatch(lines[i])) { take = i; break; }
        }
        if (take < 0) { take = Math.Min(lines.Length, HeaderFallbackLines); }
        return string.Join("\n", lines.Take(take));
    }
}
