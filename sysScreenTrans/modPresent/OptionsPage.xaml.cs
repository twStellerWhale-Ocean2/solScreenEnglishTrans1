using System.Windows.Input;
using ScreenTrans.Capture;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using RoutedEventArgs = System.Windows.RoutedEventArgs;

namespace ScreenTrans.Present;

/// <summary>
/// 選項分頁（Issue #34；原 SettingsWindow 內容移入為 UserControl）：金鑰／朗讀語音／查詢模型／
/// 喚起快捷鍵（監聽擷取）。儲存後 raise <see cref="SettingsChanged"/>；情境提示改由「情境」分頁管理，
/// 本頁不呈現但 <see cref="Gather"/> 保留既有 Context 與 HistoryMax、不重置。
/// </summary>
public partial class OptionsPage : UserControl
{
    private const string DefaultVoiceTag = "";
    private ISpeechService? _testSvc;
    private HotKeyBinding _hotkey = HotKeyBinding.Default;
    private bool _listening;

    /// <summary>目前組態（供 Gather 保留未在本頁呈現之欄位）。</summary>
    public AppConfig Config { get; private set; }

    /// <summary>按「儲存」後觸發，帶新組態；呼叫端據此重建服務、更新狀態、重註冊熱鍵。</summary>
    public event Action<AppConfig>? SettingsChanged;

    public OptionsPage(AppConfig current)
    {
        InitializeComponent();
        Config = current;

        VoiceBox.Items.Add(new ComboBoxItem { Content = "（系統預設英文語音）", Tag = DefaultVoiceTag });
        foreach (var v in SpeechService.InstalledVoiceNames())
        {
            VoiceBox.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        }

        ChangeHotkeyBtn.Click += (_, _) => StartListening();
        PreviewKeyDown += OnListenKeyDown;
        PreviewMouseDown += OnListenMouseDown;
        SaveBtn.Click += OnSave;
        TestBtn.Click += OnTest;

        ColorRulesBox.Text = NoteDefaults.ColorRules; // 智能配色規則（Issue #55）
        SetConfig(current);
    }

    /// <summary>以指定組態刷新欄位（啟動與外部變更後呼叫）。</summary>
    public void SetConfig(AppConfig c)
    {
        Config = c;
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        KeyStatus.Text = string.IsNullOrWhiteSpace(key) ? "目前狀態：○ 未設定" : "目前狀態：● 已設定";
        SelectByTag(VoiceBox, c.Voice ?? DefaultVoiceTag);
        QueryModelBox.Text = c.Model;
        _hotkey = HotKeyBinding.Parse(c.Hotkey);
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyStatus() => HotkeyStatus.Text = "目前：" + _hotkey.DisplayName;

    private void StartListening()
    {
        _listening = true;
        HotkeyStatus.Text = "請按下快捷鍵…（Esc 取消）";
        ChangeHotkeyBtn.IsEnabled = false;
        Focus();
        Keyboard.Focus(this);
    }

    private void StopListening()
    {
        _listening = false;
        ChangeHotkeyBtn.IsEnabled = true;
        UpdateHotkeyStatus();
    }

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
        _hotkey = HotKeyBinding.Keyboard(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        StopListening();
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
            _hotkey = HotKeyBinding.OfMouse(MouseTrigger.LeftRight);
            StopListening();
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
        Config.TimeoutSec,
        TagOf(VoiceBox),
        Config.MaxRetries,
        _hotkey.Serialize(),
        Config.HistoryMax,   // 保留（#13）
        Config.Context);     // 保留情境（由情境分頁管理，本頁不重置）

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
            Config = Gather();
            Config.Save(AppConfig.SettingsPath); // %APPDATA%（Issue #51 遷居；exe 旁不再寫）
            NoteDefaults.ColorRules = ColorRulesBox.Text ?? ""; // 智能配色規則（Issue #55）
            NoteDefaults.Save();
            KeyBox.Clear();
            SetConfig(Config);
            SettingsChanged?.Invoke(Config);
            System.Windows.MessageBox.Show("已儲存。", "ScreenTrans 選項");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("儲存失敗：" + ex.Message, "ScreenTrans 選項");
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
            System.Windows.MessageBox.Show("測試失敗：" + ex.Message, "ScreenTrans 選項");
        }
    }
}
