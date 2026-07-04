using System.Windows;
using System.Windows.Input;
using ScreenTrans.Capture;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace ScreenTrans.Present;

/// <summary>
/// 系統匣「設定…」視窗：設定 API 金鑰（寫使用者環境變數、不落地）與朗讀語音（Windows 內建語音，
/// 由 GetInstalledVoices 列舉）、查詢模型（寫 appsettings.json）。「測試發音」以當前設定即時試聽。
/// </summary>
public partial class SettingsWindow : Window
{
    private const string DefaultVoiceTag = ""; // 空＝系統預設英文語音
    private ISpeechService? _testSvc;
    private HotKeyBinding _hotkey = HotKeyBinding.Default;
    private bool _listening;

    /// <summary>使用者按「儲存」後的新組態；呼叫端據此重建服務。</summary>
    public AppConfig ResultConfig { get; private set; }

    public SettingsWindow(AppConfig current)
    {
        InitializeComponent();
        ResultConfig = current;

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        KeyStatus.Text = string.IsNullOrWhiteSpace(key) ? "目前狀態：○ 未設定" : "目前狀態：● 已設定";

        VoiceBox.Items.Add(new ComboBoxItem { Content = "（系統預設英文語音）", Tag = DefaultVoiceTag });
        foreach (var v in SpeechService.InstalledVoiceNames())
        {
            VoiceBox.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        }
        SelectByTag(VoiceBox, current.Voice ?? DefaultVoiceTag);
        QueryModelBox.Text = current.Model;

        _hotkey = HotKeyBinding.Parse(current.Hotkey);
        UpdateHotkeyStatus();
        ChangeHotkeyBtn.Click += (_, _) => StartListening();
        PreviewKeyDown += OnListenKeyDown;
        PreviewMouseDown += OnListenMouseDown;

        SaveBtn.Click += OnSave;
        CancelBtn.Click += (_, _) => DialogResult = false;
        TestBtn.Click += OnTest;
    }

    private void UpdateHotkeyStatus() => HotkeyStatus.Text = "目前：" + _hotkey.DisplayName;

    /// <summary>進入監聽模式：擷取下一個鍵盤組合或滑鼠鍵作為新綁定；`Esc` 取消。</summary>
    private void StartListening()
    {
        _listening = true;
        HotkeyStatus.Text = "請按下快捷鍵…（Esc 取消）";
        ChangeHotkeyBtn.IsEnabled = false;
        Keyboard.Focus(this);
    }

    private void StopListening()
    {
        _listening = false;
        ChangeHotkeyBtn.IsEnabled = true;
        UpdateHotkeyStatus();
    }

    private void OnListenKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_listening)
        {
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopListening(); // 取消、不變更
            return;
        }
        if (IsModifierKey(key))
        {
            return; // 等待主鍵
        }
        uint mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotKeyBinding.ModControl;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotKeyBinding.ModAlt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotKeyBinding.ModShift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotKeyBinding.ModWin;
        _hotkey = HotKeyBinding.Keyboard(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        StopListening();
    }

    private void OnListenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_listening)
        {
            return;
        }
        // 左右鍵同按（兩鍵皆為 Pressed）
        if (e.LeftButton == MouseButtonState.Pressed && e.RightButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
            _hotkey = HotKeyBinding.OfMouse(MouseTrigger.LeftRight);
            StopListening();
            return;
        }
        MouseTrigger? trig = e.ChangedButton switch
        {
            MouseButton.Middle => MouseTrigger.Middle,
            MouseButton.XButton1 => MouseTrigger.XButton1,
            MouseButton.XButton2 => MouseTrigger.XButton2,
            _ => null, // 單獨左/右鍵不作綁定，放行給正常 UI 操作
        };
        if (trig is null)
        {
            return;
        }
        e.Handled = true;
        _hotkey = HotKeyBinding.OfMouse(trig.Value);
        StopListening();
    }

    private static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or
        Key.System or Key.None;

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if ((string?)item.Tag == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static string TagOf(ComboBox box) => (string?)((ComboBoxItem?)box.SelectedItem)?.Tag ?? "";

    private AppConfig Gather() => new(
        string.IsNullOrWhiteSpace(QueryModelBox.Text) ? "gpt-4o-mini" : QueryModelBox.Text.Trim(),
        ResultConfig.TimeoutSec,
        TagOf(VoiceBox),
        ResultConfig.MaxRetries, // 修正：原先漏帶，存設定會把 MaxRetries 重置為預設 2
        _hotkey.Serialize());

    /// <summary>金鑰欄非空才更新；寫使用者環境變數（持久）＋本行程環境變數（即時生效）。</summary>
    private void ApplyKeyIfProvided()
    {
        var newKey = KeyBox.Password;
        if (!string.IsNullOrWhiteSpace(newKey))
        {
            newKey = newKey.Trim();
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", newKey, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", newKey, EnvironmentVariableTarget.Process);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyKeyIfProvided();
            ResultConfig = Gather();
            ResultConfig.Save(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("儲存失敗：" + ex.Message, "ScreenTrans 設定");
        }
    }

    private void OnTest(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = Gather();
            (_testSvc as IDisposable)?.Dispose();
            _testSvc = new SpeechService(cfg.Voice);
            _testSvc.Speak("Hello, this is ScreenTrans.", "en-US", stopPrevious: true);
            _testSvc.Speak("你好，這是螢幕英文翻譯工具。", "zh-TW", stopPrevious: false);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("測試失敗：" + ex.Message, "ScreenTrans 設定");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        (_testSvc as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
