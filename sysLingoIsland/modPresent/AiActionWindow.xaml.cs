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
    private bool _autoClose;      // 成功後自動關（進度提示視窗用、免 OK；失敗/取消仍留 OK）
    private bool _showCost = true; // false＝不顯費用（免費動作如搜尋進度）
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
        => RunAndShow(owner, title, action, autoCloseOnSuccess: false, showCost: true);

    /// <summary>
    /// 進度/動作對話視窗（#185）：<paramref name="autoCloseOnSuccess"/>＝true 時動作成功後自動關（免 OK，供搜尋等進度提示、減少等待焦慮）；
    /// 失敗或取消仍留 OK 供閱讀。<paramref name="showCost"/>＝false 時不顯費用（免費動作如搜尋）。
    /// </summary>
    public static void RunAndShow(Window? owner, string title,
        Func<Action<string>, System.Threading.CancellationToken, Task<IReadOnlyList<AiUsage>?>> action,
        bool autoCloseOnSuccess, bool showCost)
    {
        var dlg = new AiActionWindow(title) { Owner = owner };
        dlg._autoClose = autoCloseOnSuccess;
        dlg._showCost = showCost;
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
        var ok = false;
        try
        {
            var usages = await _action(Append, _cts.Token);
            if (_showCost) { ShowCost(usages); } else { CostText.Visibility = System.Windows.Visibility.Collapsed; }
            ok = true;
        }
        catch (OperationCanceledException) { Append("Canceled."); }
        catch (Exception ex) { Append("Failed: " + ex.Message); } // SpeakerEnrichException 訊息即人類可讀
        finally
        {
            if (ok && _autoClose) { Close(); } else { Finish(); } // 進度視窗成功即自動關；否則留 OK
        }
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
        var twd = AiCost.ToTwd(costSum);
        var tokens = $"Tokens: {inTot:N0} in + {outTot:N0} out across {usages.Count} call(s)";
        var cost = anyUnknown
            ? $"估算 AI 費用 ≈ 約 NT${twd:0.##}+（部分模型無單價）"
            : $"估算 AI 費用 ≈ 約 NT${twd:0.##}";
        if (anyWeb) { cost += "（含網搜工具費）"; }
        CostText.Text = $"{tokens}.\n{cost}\n（估算，匯率約 US$1≈NT${AiCost.UsdToTwd:0}；實際請以 OpenAI 現價與匯率為準）";
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

    /// <summary>以標題列 X／系統關閉退出時，若動作仍在跑則一併取消（等同 Cancel 鈕）——否則 <c>ShowDialog</c> 提早返回而 token 未取消，背景 async 管線會續跑續花費（增量5′ 審查修）。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_done) { _cts.Cancel(); }
        base.OnClosing(e);
    }
}
