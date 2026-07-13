namespace LingoIsland.Capture;

/// <summary>
/// 指定快捷鍵監聽期間之全域熱鍵暫停/恢復守衛（Issue #89）。選項頁進入監聽時暫停全域喚起熱鍵、
/// 結束時依現行組態恢復——避免監聽中按下與現行相同之鍵誤觸變暗遮罩／直接點選擷取，
/// 並使鍵盤組合不被 <c>RegisterHotKey</c> 攔截吞鍵而得於監聽模式正確擷取。
/// <para>
/// 本守衛集中「暫停/恢復」之**冪等與對稱**（重複進入監聽只暫停一次、對稱只恢復一次、未曾暫停不誤呼恢復），
/// 確保**恢復必達**（魔鬼代言人：暫停後不得回不來）。純邏輯、不依賴 WPF，可單元測試。
/// </para>
/// </summary>
internal sealed class HotkeyListenGuard
{
    private readonly Action _suspend;
    private readonly Action _resume;
    private bool _suspended;

    public HotkeyListenGuard(Action suspend, Action resume)
    {
        _suspend = suspend;
        _resume = resume;
    }

    /// <summary>目前是否處於暫停（監聽）狀態。</summary>
    public bool IsSuspended => _suspended;

    /// <summary>
    /// 監聽開始（<paramref name="listening"/>＝<c>true</c>）暫停一次、結束（<c>false</c>）恢復一次；冪等且對稱。
    /// </summary>
    public void OnListeningChanged(bool listening)
    {
        if (listening)
        {
            if (_suspended)
            {
                return; // 已暫停，重複進入監聽不重覆暫停
            }
            _suspended = true;
            _suspend();
        }
        else
        {
            if (!_suspended)
            {
                return; // 未曾暫停，不誤呼恢復
            }
            _suspended = false;
            _resume();
        }
    }
}
