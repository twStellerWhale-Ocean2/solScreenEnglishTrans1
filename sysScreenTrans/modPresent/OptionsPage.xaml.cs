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
    private bool _listening; // 是否正在監聽擷取喚起快捷鍵

    /// <summary>目前組態（供 Gather 保留未在本頁呈現之欄位）。</summary>
    public AppConfig Config { get; private set; }

    /// <summary>按「儲存」後觸發，帶新組態；呼叫端據此重建服務、更新狀態、重註冊熱鍵。</summary>
    public event Action<AppConfig>? SettingsChanged;

    /// <summary>
    /// 監聽指定快捷鍵之開始（<c>true</c>）／結束（<c>false</c>）；呼叫端（App）據此暫停/恢復全域熱鍵，
    /// 避免監聽期間按下與現行相同之鍵誤觸喚起，並讓鍵盤組合不被 <c>RegisterHotKey</c> 吞鍵（Issue #89）。
    /// </summary>
    public event Action<bool>? ListeningChanged;

    public OptionsPage(AppConfig current)
    {
        InitializeComponent();
        Config = current;

        VoiceBox.Items.Add(new ComboBoxItem { Content = "(System default English voice)", Tag = DefaultVoiceTag });
        foreach (var v in SpeechService.InstalledVoiceNames())
        {
            VoiceBox.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        }

        ChangeHotkeyBtn.Click += (_, _) => StartListening();
        PreviewKeyDown += OnListenKeyDown;
        PreviewMouseDown += OnListenMouseDown;
        // 監聽中若焦點離開本頁（切分頁/切視窗而未擷取或未按 Esc）→ 視同取消，確保全域熱鍵必恢復（Issue #89）
        LostKeyboardFocus += (_, _) => StopListening();
        SaveBtn.Click += OnSave;
        TestBtn.Click += OnTest;
        // 發音及格門檻：滑桿↔數值框雙向同步（spec#10）
        PronThresholdSlider.ValueChanged += (_, e) => PronThresholdBox.Text = ((int)e.NewValue).ToString();
        PronThresholdBox.LostFocus += (_, _) => SyncThresholdFromBox();
        // 條目字級：滑桿↔數值框雙向同步（#複查）
        EntryFontSlider.ValueChanged += (_, e) => EntryFontBox.Text = ((int)e.NewValue).ToString();
        EntryFontBox.LostFocus += (_, _) => SyncEntryFontFromBox();

        SetConfig(current);
    }

    /// <summary>以指定組態刷新欄位（啟動與外部變更後呼叫）。</summary>
    public void SetConfig(AppConfig c)
    {
        Config = c;
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        KeyStatus.Text = string.IsNullOrWhiteSpace(key) ? "Status: ○ Not set" : "Status: ● Set";
        SelectByTag(VoiceBox, c.Voice ?? DefaultVoiceTag);
        QueryModelBox.Text = c.Model;
        PronThresholdSlider.Value = c.PronPassThreshold; // ValueChanged 同步數值框
        PronThresholdBox.Text = c.PronPassThreshold.ToString();
        PronModelBox.Text = c.PronModel;
        EntryFontSlider.Value = c.EntryFontSize; // ValueChanged 同步數值框（#複查）
        EntryFontBox.Text = ((int)c.EntryFontSize).ToString();
        EntryBoldChk.IsChecked = c.EntryBold;
        EntryWrapChk.IsChecked = c.EntryWrap;
        _hotkey = HotKeyBinding.Parse(c.Hotkey);
        UpdateHotkeyStatus();
    }

    /// <summary>條目字級數值框 → 滑桿同步（鉗制 12–32；空/非數字回預設）。</summary>
    private void SyncEntryFontFromBox()
    {
        var v = int.TryParse(EntryFontBox.Text?.Trim(), out var n) ? Math.Clamp(n, 12, 32) : (int)AppConfig.DefaultEntryFontSize;
        EntryFontSlider.Value = v;
    }

    private void UpdateHotkeyStatus()
    {
        HotkeyStatus.Text = "Current: " + _hotkey.DisplayName;
    }

    private void StartListening()
    {
        _listening = true;
        ListeningChanged?.Invoke(true); // 暫停全域熱鍵，避免監聽期間按現行鍵誤觸喚起（Issue #89）
        HotkeyStatus.Text = "Press a hotkey… (Esc to cancel)";
        ChangeHotkeyBtn.IsEnabled = false;
        Focus();
        Keyboard.Focus(this);
    }

    private void StopListening()
    {
        if (!_listening)
        {
            return; // 已非監聽（如 LostKeyboardFocus 於非監聽時觸發）→ 不重覆恢復
        }
        _listening = false;
        ChangeHotkeyBtn.IsEnabled = true;
        UpdateHotkeyStatus();
        ListeningChanged?.Invoke(false); // 恢復全域熱鍵（Issue #89）
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

    /// <summary>把擷取到的綁定寫入喚起快捷鍵，並結束監聽。</summary>
    private void SetListenedBinding(HotKeyBinding binding)
    {
        _hotkey = binding;
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
        Config.Context,      // 保留情境（由情境分頁管理，本頁不重置）
        (int)PronThresholdSlider.Value, // 發音及格門檻（spec#10）
        string.IsNullOrWhiteSpace(PronModelBox.Text) ? AppConfig.DefaultPronModel : PronModelBox.Text.Trim(),
        EntryFontSlider.Value,          // 條目字級（#複查）
        EntryBoldChk.IsChecked == true, // 條目粗體
        EntryWrapChk.IsChecked == true); // 條目自動換行

    /// <summary>數值框 → 滑桿同步（鉗制 0–100；空/非數字回門檻預設）。</summary>
    private void SyncThresholdFromBox()
    {
        var v = int.TryParse(PronThresholdBox.Text?.Trim(), out var n) ? Math.Clamp(n, 0, 100) : AppConfig.DefaultPronThreshold;
        PronThresholdSlider.Value = v; // ValueChanged 會把數值框回寫為整數
    }

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
            KeyBox.Clear();
            SetConfig(Config);
            SettingsChanged?.Invoke(Config);
            System.Windows.MessageBox.Show("Saved.", "ScreenTrans Options");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Save failed: " + ex.Message, "ScreenTrans Options");
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
            _testSvc.Speak("Hello, this is the screen English lookup tool.", "en-US", stopPrevious: false);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Test failed: " + ex.Message, "ScreenTrans Options");
        }
    }
}
