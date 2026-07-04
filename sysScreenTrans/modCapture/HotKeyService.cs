using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenTrans.Capture;

/// <summary>
/// 全域喚起快捷鍵服務（[modCapture模組] 喚起快捷鍵契約、spec#1）。依綁定型別選後端：
/// <list type="bullet">
/// <item>鍵盤組合 → Win32 <c>RegisterHotKey</c>（**不使用低階鍵盤 hook**、對全系統鍵盤輸入零延遲）。</item>
/// <item>滑鼠鍵（中鍵／側鍵／左右同按）→ 低階滑鼠 hook <c>WH_MOUSE_LL</c>；callback **僅比對當前綁定、
/// 一律 <c>CallNextHookEx</c> 放行**（不吞、不改寫輸入），觸發改以 Dispatcher 非同步喚起、不阻塞 hook 迴圈。</item>
/// </list>
/// 兩後端對外統一以 <see cref="HotKeyPressed"/> 事件呈現，喚起接線不因綁定型別而異。程式結束確保釋放。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotKeyId = 0x4C4C;       // 任意唯一 id
    private const uint MOD_NOREPEAT = 0x4000;  // 按住不連發
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // 低階滑鼠 hook 相關
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const uint XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int ptX;
        public int ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private HwndSource? _source;
    private bool _registered;
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? _mouseProc; // 保留委派參考，避免 GC 回收致 callback 失效
    private Dispatcher? _dispatcher;
    private HotKeyBinding _binding = HotKeyBinding.Default;
    private bool _leftDown, _rightDown;

    /// <summary>喚起快捷鍵觸發時引發（於 UI 執行緒）。</summary>
    public event Action? HotKeyPressed;

    /// <summary>以指定綁定註冊喚起快捷鍵。成功回 true；失敗（被占用／hook 安裝失敗）回 false。</summary>
    public bool Register(HotKeyBinding binding)
    {
        Unregister();
        _binding = binding;
        _dispatcher = Dispatcher.CurrentDispatcher;
        return binding.Kind == HotKeyKind.Mouse ? RegisterMouse() : RegisterKeyboard(binding);
    }

    /// <summary>相容多載：以預設綁定（Alt+L）註冊。</summary>
    public bool Register() => Register(HotKeyBinding.Default);

    private bool RegisterKeyboard(HotKeyBinding b)
    {
        if (_source is null)
        {
            var parameters = new HwndSourceParameters("ScreenTransHotKeyWindow") { ParentWindow = HWND_MESSAGE };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }
        _registered = RegisterHotKey(_source.Handle, HotKeyId, b.Modifiers | MOD_NOREPEAT, b.VirtualKey);
        return _registered;
    }

    private bool RegisterMouse()
    {
        _leftDown = _rightDown = false;
        _mouseProc = MouseProc; // 存欄位保參考
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        return _mouseHook != IntPtr.Zero;
    }

    /// <summary>解除當前綁定（重新設定快捷鍵時先解除舊綁定）。</summary>
    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotKeyId);
            _registered = false;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _mouseProc = null;
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

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && MatchesBinding(wParam.ToInt32(), lParam))
        {
            // hook callback 須輕量：不在此執行喚起主動線，改排入 UI 佇列非同步觸發
            _dispatcher?.BeginInvoke(() => HotKeyPressed?.Invoke());
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam); // 一律放行、不吞事件
    }

    /// <summary>比對本次滑鼠訊息是否命中當前綁定；左右同按於命中後重置狀態避免連發。</summary>
    private bool MatchesBinding(int msg, IntPtr lParam)
    {
        switch (_binding.Mouse)
        {
            case MouseTrigger.Middle:
                return msg == WM_MBUTTONDOWN;
            case MouseTrigger.XButton1:
                return msg == WM_XBUTTONDOWN && HiWord(lParam) == XBUTTON1;
            case MouseTrigger.XButton2:
                return msg == WM_XBUTTONDOWN && HiWord(lParam) == XBUTTON2;
            case MouseTrigger.LeftRight:
                switch (msg)
                {
                    case WM_LBUTTONDOWN: _leftDown = true; break;
                    case WM_LBUTTONUP: _leftDown = false; break;
                    case WM_RBUTTONDOWN: _rightDown = true; break;
                    case WM_RBUTTONUP: _rightDown = false; break;
                }
                if (_leftDown && _rightDown)
                {
                    _leftDown = _rightDown = false;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static uint HiWord(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        return (data.mouseData >> 16) & 0xFFFF;
    }

    public void Dispose()
    {
        Unregister();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
