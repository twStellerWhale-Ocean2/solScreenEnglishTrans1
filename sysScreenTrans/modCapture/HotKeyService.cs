using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenTrans.Capture;

/// <summary>
/// 全域熱鍵服務。採 Win32 <c>RegisterHotKey</c>（[runWi自訂Usr熱鍵喚起框選] 之喚起機制、
/// [modCapture模組] 熱鍵契約）——**不使用低階鍵盤 hook**，對全系統輸入零延遲影響（spec#1）。
/// MOD_ALT 不分左右，故 Alt+L 左右 Alt 皆可觸發。
/// 系統匣應用無主視窗，以 message-only window（HWND_MESSAGE）接收 WM_HOTKEY。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotKeyId = 0x4C4C;      // 任意唯一 id
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000; // 按住不連發
    private const uint VK_L = 0x4C;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private bool _registered;

    /// <summary>熱鍵按下時觸發（於 UI 執行緒）。</summary>
    public event Action? HotKeyPressed;

    /// <summary>註冊 Alt+L。成功回 true；被占用等失敗回 false（呼叫端應提示）。</summary>
    public bool Register()
    {
        var parameters = new HwndSourceParameters("ScreenTransHotKeyWindow")
        {
            ParentWindow = HWND_MESSAGE,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(_source.Handle, HotKeyId, MOD_ALT | MOD_NOREPEAT, VK_L);
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            HotKeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            if (_registered)
            {
                UnregisterHotKey(_source.Handle, HotKeyId);
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
