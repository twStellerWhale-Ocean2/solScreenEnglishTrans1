using LingoIsland.Query;
using UserControl = System.Windows.Controls.UserControl;

namespace LingoIsland.Present;

/// <summary>
/// Dictionary 分頁（#135）：取代原浮動 <c>ResultWindow</c>，宿於獨立字典視窗。<b>薄殼</b>——實際內容全在共用
/// <see cref="ResultView"/>：頂部工具列（導航/編輯/加入至）、其下**可編輯下拉查詢輸入列**（v1.0.1 兩列對調後移入 ResultView，
/// 觸發 <see cref="ManualQueryRequested"/>／<see cref="HistoryRequested"/>）、三欄結果、逐字查字、編輯重譯、加入筆記。
/// 本頁僅將 ResultView 的事件轉發給 App、並把 App 呼叫委派給 ResultView，維持既有對外介面不變（App 端不受重排影響）。
/// 整頁**透明底**＝透出主視窗粉底與浮水印、控制項採全域半透明白樣式，與其他分頁一致（USR 回饋）。
/// </summary>
public partial class DictionaryPage : UserControl
{
    /// <summary>底部「加入我的筆記」或「自動加入筆記」時觸發（轉發自 <see cref="ResultView"/>）。</summary>
    public event Action<NoteAddRequest>? AddToNotesRequested;

    /// <summary>結果內點單字查該字時觸發（轉發自 <see cref="ResultView"/>）。</summary>
    public event Action<string>? WordQueryRequested;

    /// <summary>編輯原文重譯時觸發（轉發自 <see cref="ResultView"/>）。</summary>
    public event Action<string>? TextReQueryRequested;

    /// <summary>頂部輸入框按「Look up」或 Enter 時觸發（帶輸入之英文原文，App 依單字/整句決定查字義或翻譯；轉發自 <see cref="ResultView"/>）。</summary>
    public event Action<string>? ManualQueryRequested;

    /// <summary>輸入下拉開啟時觸發（#135 回饋）：App 據此以查詢歷史填入下拉選單（轉發自 <see cref="ResultView"/>）。</summary>
    public event Action? HistoryRequested;

    public DictionaryPage()
    {
        InitializeComponent();
        // 轉發內層 ResultView 事件給 App（單一接線點；輸入列 v1.0.1 已移入 ResultView，故查詢/歷史事件亦自此轉發）
        Result.AddToNotesRequested += r => AddToNotesRequested?.Invoke(r);
        Result.WordQueryRequested += w => WordQueryRequested?.Invoke(w);
        Result.TextReQueryRequested += t => TextReQueryRequested?.Invoke(t);
        Result.ManualQueryRequested += t => ManualQueryRequested?.Invoke(t);
        Result.HistoryRequested += () => HistoryRequested?.Invoke();
    }

    /// <summary>以查詢歷史（英文原文、新在前、去重）填入輸入下拉；保留使用者當前已鍵入文字（委派 <see cref="ResultView"/>）。</summary>
    public void SetHistory(IEnumerable<string> originals) => Result.SetHistory(originals);

    /// <summary>是否已顯示過非空結果（供 App「喚回」判斷分頁是否已有內容）。</summary>
    public bool HasResult => Result.HasResult;

    /// <summary>設定變更後由 App 注入新語音服務（避免播放鈕用到已釋放的舊服務）。</summary>
    public void UpdateSpeech(ISpeechService speech) => Result.UpdateSpeech(speech);

    /// <summary>設定「加入至」下拉來源（顯示結果前呼叫）。</summary>
    public void SetNoteTargets(IEnumerable<string> topFolderNames, string activeContextName)
        => Result.SetNoteTargets(topFolderNames, activeContextName);

    public void ShowLoading() => Result.ShowLoading();

    public void ShowResult(QueryResult r, ISpeechService speech) => Result.ShowResult(r, speech);

    public void ShowError(string message) => Result.ShowError(message);

    public void PushWordResult(QueryResult r) => Result.PushWordResult(r);

    public void ReplaceCurrentResult(QueryResult r) => Result.ReplaceCurrentResult(r);

    public void WordLookupFailed() => Result.WordLookupFailed();
}
