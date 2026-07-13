using System;
using System.Threading;

namespace LingoIsland;

/// <summary>
/// 單一實例守衛：以「使用者範圍具名 Mutex 是否為本呼叫所建」判定本進程是否為第一個實例。
/// 首個實例持有 Mutex 控制代碼整個生命週期；控制代碼存在即代表「已在執行」，
/// 故後續啟動之 <see cref="Acquire"/> 會得 <see cref="IsFirstInstance"/>=false，呼叫端應提示並結束。
/// 對應 design ＜II.C 模組層＞單一實例 invariant、＜setWi自訂Usr啟動結束常駐＞驗收 row02。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>使用者/工作階段範圍（<c>Local\</c>）具名，避免跨 session 誤判既有實例。</summary>
    internal const string DefaultMutexName = @"Local\LingoIsland.SingleInstance";

    private Mutex? _mutex;

    /// <summary>本實例是否為第一個（具名 Mutex 由本呼叫所建）。</summary>
    public bool IsFirstInstance { get; private init; }

    private SingleInstanceGuard() { }

    /// <summary>
    /// 開啟（或建立）具名 Mutex 並持有其控制代碼。若為本呼叫首建 → <see cref="IsFirstInstance"/>=true；
    /// 若既有實例仍持有同名 Mutex → false。不依賴 Mutex 擁有權（thread-affine），純以命名物件存在與否偵測。
    /// </summary>
    public static SingleInstanceGuard Acquire(string mutexName = DefaultMutexName)
    {
        // initiallyOwned:false —— 只需控制代碼存在即可讓命名物件存活，不涉擁有權/釋放之執行緒親和性。
        var mutex = new Mutex(initiallyOwned: false, mutexName, out bool createdNew);
        return new SingleInstanceGuard { _mutex = mutex, IsFirstInstance = createdNew };
    }

    /// <summary>釋放本實例持有的控制代碼；首個實例釋放後命名物件消滅，下次啟動即再成為第一實例。</summary>
    public void Dispose()
    {
        _mutex?.Dispose();
        _mutex = null;
    }
}
