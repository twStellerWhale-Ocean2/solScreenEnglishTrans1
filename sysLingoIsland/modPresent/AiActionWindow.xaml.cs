using System.Windows;
using LingoIsland.Video;

namespace LingoIsland.Present;

/// <summary>
/// AI 分析／Web 搜尋等動作之進度對話視窗（顯示動作訊息與**估算 AI 費用**）：於 Loaded 執行 <see cref="Start"/> 設定之動作、
/// 逐步 report 訊息，完成／失敗／取消後鈕變「OK」關閉；期間可「Cancel」。以 <c>ShowDialog</c> 模態呈現——其訊息迴圈
/// 會續泵 dispatcher，故動作內之 async await 照常推進、UI 亦即時更新。
/// </summary>
public partial class AiActionWindow : Window
{
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private bool _done;
    private Func<Action<string>, System.Threading.CancellationToken, Task<IReadOnlyList<AiUsage>?>>? _action;

    /// <summary>一次 AI 呼叫之用量（供費用顯示）：輸入/輸出 tokens＋模型＋是否含 web_search 工具費。</summary>
    public sealed record AiUsage(int InputTokens, int OutputTokens, string Model, bool WebSearch = false);

    public AiActionWindow(string title)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = title;
        ActionBtn.Click += OnActionBtn;
        Loaded += async (_, _) => await RunAsync();
    }

    /// <summary>設定要執行的 AI 動作（回傳用量供費用顯示；null＝無用量/被丟棄）。於 <c>ShowDialog</c> 前呼叫。</summary>
    public void Start(Func<Action<string>, System.Threading.CancellationToken, Task<IReadOnlyList<AiUsage>?>> action) => _action = action;

    /// <summary>建立、設定動作並模態顯示；動作於視窗內執行、完成後由使用者按 OK 關閉。</summary>
    public static void RunAndShow(Window? owner, string title,
        Func<Action<string>, System.Threading.CancellationToken, Task<IReadOnlyList<AiUsage>?>> action)
    {
        var dlg = new AiActionWindow(title) { Owner = owner };
        dlg.Start(action);
        dlg.ShowDialog();
    }

    private void Append(string line)
    {
        MsgText.Text = string.IsNullOrEmpty(MsgText.Text) ? line : MsgText.Text + "\n" + line;
        MsgScroll.ScrollToEnd();
    }

    private async Task RunAsync()
    {
        if (_action is null) { Finish(); return; }
        try
        {
            ShowCost(await _action(Append, _cts.Token));
        }
        catch (OperationCanceledException) { Append("Canceled."); }
        catch (Exception ex) { Append("Failed: " + ex.Message); } // SpeakerEnrichException 訊息即人類可讀
        finally { Finish(); }
    }

    private void ShowCost(IReadOnlyList<AiUsage>? usages)
    {
        if (usages is null || usages.Count == 0) { CostText.Text = "AI cost: no usage was reported."; return; }
        var inTot = usages.Sum(u => u.InputTokens);
        var outTot = usages.Sum(u => u.OutputTokens);
        double costSum = 0; var anyUnknown = false; var anyWeb = false;
        foreach (var u in usages)
        {
            var est = AiCost.EstimateUsd(u.Model, u.InputTokens, u.OutputTokens, u.WebSearch);
            if (est is null) { anyUnknown = true; } else { costSum += est.Value; }
            if (u.WebSearch) { anyWeb = true; }
        }
        var tokens = $"Tokens: {inTot:N0} in + {outTot:N0} out across {usages.Count} call(s)";
        var cost = anyUnknown
            ? $"Est. AI cost ≈ US${costSum:0.#####}+ (some models had no rate on file)"
            : $"Est. AI cost ≈ US${costSum:0.#####}";
        if (anyWeb) { cost += " (incl. web-search fee)"; }
        CostText.Text = $"{tokens}.\n{cost}.\n(Estimate only — check OpenAI for current rates.)";
    }

    private void Finish()
    {
        _done = true;
        ActionBtn.Content = "OK";
        ActionBtn.IsEnabled = true;
    }

    private void OnActionBtn(object sender, RoutedEventArgs e)
    {
        if (_done) { Close(); return; }
        _cts.Cancel();               // 取消進行中動作；動作結束（OperationCanceledException）→ Finish → OK
        ActionBtn.IsEnabled = false; // 取消中：防重複點；Finish 會再啟用為 OK
    }
}
