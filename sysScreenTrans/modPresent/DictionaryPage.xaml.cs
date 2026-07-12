using ScreenTrans.Query;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace ScreenTrans.Present;

/// <summary>
/// Dictionary 分頁（#135）：查詢結果併入主視窗成為一個分頁，取代原浮動 <c>ResultWindow</c>。頂部**可編輯下拉**可
/// 手動輸入英文查詢（觸發 <see cref="ManualQueryRequested"/>，App 依單字/整句走查字義或翻譯），下拉並列出查詢歷史
/// （開啟時經 <see cref="HistoryRequested"/> 向 App 取得）。下方宿共用 <see cref="ResultView"/> 呈現三欄結果、逐字查字、
/// 編輯重譯、加入筆記。整頁**透明底**＝透出主視窗粉底與浮水印、控制項採全域半透明白樣式，與其他分頁一致（USR 回饋）。
/// </summary>
public partial class DictionaryPage : UserControl
{
    /// <summary>底部「加入我的筆記」或「自動加入筆記」時觸發（轉發自 <see cref="ResultView"/>）。</summary>
    public event Action<NoteAddRequest>? AddToNotesRequested;

    /// <summary>結果內點單字查該字時觸發（轉發自 <see cref="ResultView"/>）。</summary>
    public event Action<string>? WordQueryRequested;

    /// <summary>編輯原文重譯時觸發（轉發自 <see cref="ResultView"/>）。</summary>
    public event Action<string>? TextReQueryRequested;

    /// <summary>頂部輸入框按「Look up」或 Enter 時觸發（帶輸入之英文原文，App 依單字/整句決定查字義或翻譯）。</summary>
    public event Action<string>? ManualQueryRequested;

    /// <summary>輸入下拉開啟時觸發（#135 回饋）：App 據此以查詢歷史填入下拉選單。</summary>
    public event Action? HistoryRequested;

    public DictionaryPage()
    {
        InitializeComponent();
        // 轉發內層 ResultView 事件給 App（單一接線點）
        Result.AddToNotesRequested += r => AddToNotesRequested?.Invoke(r);
        Result.WordQueryRequested += w => WordQueryRequested?.Invoke(w);
        Result.TextReQueryRequested += t => TextReQueryRequested?.Invoke(t);

        LookupBtn.Click += (_, _) => DoLookup();
        InputBox.KeyDown += OnInputKeyDown;
        InputBox.DropDownOpened += (_, _) => HistoryRequested?.Invoke(); // 開下拉即向 App 取查詢歷史填入
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DoLookup();
            e.Handled = true;
        }
    }

    private void DoLookup()
    {
        var t = (InputBox.Text ?? "").Trim();
        if (t.Length == 0)
        {
            return;
        }
        ManualQueryRequested?.Invoke(t);
    }

    /// <summary>以查詢歷史（英文原文、新在前、去重）填入輸入下拉；保留使用者當前已鍵入文字（#135 回饋）。</summary>
    public void SetHistory(IEnumerable<string> originals)
    {
        var text = InputBox.Text;
        InputBox.ItemsSource = originals?.ToList();
        InputBox.Text = text; // 換 ItemsSource 不清掉使用者已輸入內容
    }

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
