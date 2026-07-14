using System.Windows.Input;
using LingoIsland.Capture;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace LingoIsland.Present;

/// <summary>
/// 螢幕截圖分頁（epic #145 增量2，自 <c>ContextPage</c> 拆出）：手動擷取鈕（Capture Screen）＋喚起快捷鍵狀態／變更（監聽）。
/// 事件供 App 接：<see cref="CaptureRequested"/>（手動擷取）、<see cref="HotkeyChanged"/>（持久化／重註冊）、
/// <see cref="ListeningChanged"/>（暫停／恢復全域熱鍵）。監聽於宿主視窗層掛載（#127，不依賴本頁鍵盤焦點）。
/// </summary>
public partial class ScreenCapturePage : UserControl
{
    // 喚起快捷鍵設定（#133：自選項頁整套搬入；保留 #127 視窗層擷取寫法）
    private HotKeyBinding _hotkey = HotKeyBinding.Default;
    private bool _listening; // 是否正在監聽擷取喚起快捷鍵
    private System.Windows.Window? _listenWindow; // 監聽期間掛載擷取事件之宿主視窗

    /// <summary>手動觸發螢幕擷取（#5：擷取頁「Capture Screen」鈕）；呼叫端收合主視窗後走既有喚起主動線。</summary>
    public event Action? CaptureRequested;

    /// <summary>
    /// 監聽指定快捷鍵之開始（<c>true</c>）／結束（<c>false</c>）；呼叫端（App）據此暫停／恢復全域熱鍵，
    /// 避免監聽期間按下與現行相同之鍵誤觸喚起（Issue #89）。
    /// </summary>
    public event Action<bool>? ListeningChanged;

    /// <summary>擷取到新綁定時觸發（#133）；呼叫端據此持久化 <c>Hotkey</c>、重註冊全域熱鍵、更新狀態。</summary>
    public event Action<HotKeyBinding>? HotkeyChanged;

    public ScreenCapturePage(string initialHotkey)
    {
        InitializeComponent();

        // Change 起手監聽、Capture Screen 手動擷取（#133）；監聽於宿主視窗層掛載（見 StartListening，#127），
        // 監聽中本頁被切離（切分頁致 Unloaded）→ 視同取消，確保全域熱鍵必恢復（Issue #89）。
        ChangeHotkeyBtn.Click += (_, _) => StartListening();
        CaptureScreenBtn.Click += (_, _) => CaptureRequested?.Invoke();
        Unloaded += (_, _) => StopListening();

        _hotkey = HotKeyBinding.Parse(initialHotkey); // 目前快捷鍵初值（自 AppConfig.Hotkey）
        UpdateHotkeyStatus();
    }

    // ---- 喚起快捷鍵監聽（#133；保留 #127 視窗層擷取寫法、不依賴本頁鍵盤焦點） ----

    private void UpdateHotkeyStatus()
    {
        HotkeyStatus.Text = "Current: " + _hotkey.DisplayName;
    }

    private void StartListening()
    {
        if (_listening)
        {
            return; // 已在監聽 → 不重入
        }
        _listening = true;
        ListeningChanged?.Invoke(true); // 暫停全域熱鍵，避免監聽期間按現行鍵誤觸喚起（Issue #89）
        HotkeyStatus.Text = "Press a hotkey… (Esc to cancel)";
        ChangeHotkeyBtn.IsEnabled = false;
        // 於宿主視窗層擷取按鍵/滑鼠——不依賴 UserControl 取得鍵盤焦點（修 #127）。
        _listenWindow = System.Windows.Window.GetWindow(this);
        if (_listenWindow is not null)
        {
            _listenWindow.PreviewKeyDown += OnListenKeyDown;
            _listenWindow.PreviewMouseDown += OnListenMouseDown;
            _listenWindow.Deactivated += OnListenAborted; // 切到他視窗＝取消監聽、恢復全域熱鍵
        }
        Focus();
        Keyboard.Focus(this);
    }

    private void StopListening()
    {
        if (!_listening)
        {
            return; // 已非監聽 → 不重覆恢復
        }
        _listening = false;
        ChangeHotkeyBtn.IsEnabled = true;
        UpdateHotkeyStatus();
        if (_listenWindow is not null)
        {
            _listenWindow.PreviewKeyDown -= OnListenKeyDown;
            _listenWindow.PreviewMouseDown -= OnListenMouseDown;
            _listenWindow.Deactivated -= OnListenAborted;
            _listenWindow = null;
        }
        ListeningChanged?.Invoke(false); // 恢復全域熱鍵（Issue #89）
    }

    private void OnListenAborted(object? sender, System.EventArgs e) => StopListening();

    private void OnListenKeyDown(object sender, KeyEventArgs e)
    {
        if (!_listening)
        {
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopListening();
            return;
        }
        if (IsModifierKey(key))
        {
            return;
        }
        uint mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotKeyBinding.ModControl;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotKeyBinding.ModAlt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotKeyBinding.ModShift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotKeyBinding.ModWin;
        SetListenedBinding(HotKeyBinding.Keyboard(mods, (uint)KeyInterop.VirtualKeyFromKey(key)));
    }

    private void OnListenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_listening)
        {
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed && e.RightButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
            SetListenedBinding(HotKeyBinding.OfMouse(MouseTrigger.LeftRight));
            return;
        }
        MouseTrigger? trig = e.ChangedButton switch
        {
            MouseButton.Middle => MouseTrigger.Middle,
            MouseButton.XButton1 => MouseTrigger.XButton1,
            MouseButton.XButton2 => MouseTrigger.XButton2,
            _ => null,
        };
        if (trig is null)
        {
            return;
        }
        e.Handled = true;
        SetListenedBinding(HotKeyBinding.OfMouse(trig.Value));
    }

    /// <summary>把擷取到的綁定寫入喚起快捷鍵、通知呼叫端持久化＋重註冊（#133），並結束監聽。</summary>
    private void SetListenedBinding(HotKeyBinding binding)
    {
        _hotkey = binding;
        HotkeyChanged?.Invoke(binding); // App 據此存 config、重註冊全域熱鍵、resync 選項頁快照
        StopListening();
    }

    private static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or
        Key.System or Key.None;
}
