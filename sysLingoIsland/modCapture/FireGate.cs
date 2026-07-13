namespace LingoIsland.Capture;

/// <summary>
/// 觸發收斂閘（[modCapture模組] 喚起契約，Issue #32）：低階滑鼠 hook 於短時間可能連續命中，
/// 以本閘收斂為「handler 尚未跑完前不重複派工」，避免事件風暴造成視窗重入操作。
/// <see cref="TryArm"/> 首次回 true（呼叫端派工）、其後回 false 直到 <see cref="Release"/>。執行緒安全。
/// </summary>
internal sealed class FireGate
{
    private int _pending; // 0＝閒置、1＝已派工待處理

    /// <summary>嘗試佔用：由閒置轉待處理回 true（應派工）；已待處理回 false（略過）。</summary>
    public bool TryArm() => System.Threading.Interlocked.CompareExchange(ref _pending, 1, 0) == 0;

    /// <summary>釋放：handler 執行時呼叫，允許下一次觸發。</summary>
    public void Release() => System.Threading.Interlocked.Exchange(ref _pending, 0);
}
