using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using RoutedEventArgs = System.Windows.RoutedEventArgs;

namespace ScreenTrans.Present;

/// <summary>
/// 選項分頁（Issue #34；原 SettingsWindow 內容移入為 UserControl）：金鑰／朗讀語音／查詢模型等偏好。
/// 儲存後 raise <see cref="SettingsChanged"/>；情境提示改由「情境」分頁管理、喚起快捷鍵改由「螢幕擷取」
/// 分頁管理（#133），本頁不呈現該二者，但 <see cref="Gather"/> 保留既有 Context／HistoryMax／Hotkey、不重置。
/// </summary>
public partial class OptionsPage : UserControl
{
    private const string DefaultVoiceTag = "";
    private ISpeechService? _testSvc;

    /// <summary>目前組態（供 Gather 保留未在本頁呈現之欄位）。</summary>
    public AppConfig Config { get; private set; }

    /// <summary>按「儲存」後觸發，帶新組態；呼叫端據此重建服務、更新狀態、重註冊熱鍵。</summary>
    public event Action<AppConfig>? SettingsChanged;

    public OptionsPage(AppConfig current)
    {
        InitializeComponent();
        Config = current;

        VoiceBox.Items.Add(new ComboBoxItem { Content = "(System default English voice)", Tag = DefaultVoiceTag });
        foreach (var v in SpeechService.InstalledVoiceNames())
        {
            VoiceBox.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        }

        SaveBtn.Click += OnSave;
        TestBtn.Click += OnTest;
        // 發音及格門檻：滑桿↔數值框雙向同步（spec#10）
        PronThresholdSlider.ValueChanged += (_, e) => PronThresholdBox.Text = ((int)e.NewValue).ToString();
        PronThresholdBox.LostFocus += (_, _) => SyncThresholdFromBox();
        // 條目字級：滑桿↔數值框雙向同步（#複查）
        EntryFontSlider.ValueChanged += (_, e) => EntryFontBox.Text = ((int)e.NewValue).ToString();
        EntryFontBox.LostFocus += (_, _) => SyncEntryFontFromBox();
        // 查詢視窗字級：滑桿↔數值框雙向同步（#複查）
        ResultFontSlider.ValueChanged += (_, e) => ResultFontBox.Text = ((int)e.NewValue).ToString();
        ResultFontBox.LostFocus += (_, _) => SyncResultFontFromBox();

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
        PassedTransparentChk.IsChecked = c.PassedCardTransparent; // 過關卡透明底（#123）
        ResultHideOnBlurChk.IsChecked = c.ResultHideOnBlur; // 查詢視窗失焦自動隱藏（#複查）
        ResultFontSlider.Value = c.ResultFontSize; // ValueChanged 同步數值框（#複查）
        ResultFontBox.Text = ((int)c.ResultFontSize).ToString();
    }

    /// <summary>
    /// 是否有未儲存變更（#複查）：任一欄位值異於上次儲存的 <see cref="Config"/>，或已輸入尚未套用的金鑰。
    /// 供主視窗於離開選項頁前提示。以 <see cref="Gather"/> 對上次儲存快照做記錄式結構比對。
    /// </summary>
    public bool IsDirty => !string.IsNullOrEmpty(KeyBox.Password) || Gather() != Config;

    /// <summary>捨棄未儲存變更、還原為上次儲存值（#複查：離開頁時選「確定離開」則還原）。</summary>
    public void RevertChanges()
    {
        KeyBox.Clear();
        SetConfig(Config); // 以上次儲存快照重填所有欄位
    }

    /// <summary>條目字級數值框 → 滑桿同步（鉗制 12–32；空/非數字回預設）。</summary>
    private void SyncEntryFontFromBox()
    {
        var v = int.TryParse(EntryFontBox.Text?.Trim(), out var n) ? Math.Clamp(n, 12, 32) : (int)AppConfig.DefaultEntryFontSize;
        EntryFontSlider.Value = v;
    }

    /// <summary>查詢視窗字級數值框 → 滑桿同步（鉗制 16–40；空/非數字回預設）。</summary>
    private void SyncResultFontFromBox()
    {
        var v = int.TryParse(ResultFontBox.Text?.Trim(), out var n) ? Math.Clamp(n, 16, 40) : (int)AppConfig.DefaultResultFontSize;
        ResultFontSlider.Value = v;
    }

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
        Config.Hotkey,       // 保留：喚起快捷鍵改由「螢幕擷取」分頁管理（#133），本頁不重置
        Config.HistoryMax,   // 保留（#13）
        Config.Context,      // 保留情境（由情境分頁管理，本頁不重置）
        (int)PronThresholdSlider.Value, // 發音及格門檻（spec#10）
        string.IsNullOrWhiteSpace(PronModelBox.Text) ? AppConfig.DefaultPronModel : PronModelBox.Text.Trim(),
        EntryFontSlider.Value,          // 條目字級（#複查）
        EntryBoldChk.IsChecked == true, // 條目粗體
        EntryWrapChk.IsChecked == true, // 條目自動換行
        ResultFontSlider.Value,          // 查詢視窗基準字級（#複查）
        ResultHideOnBlurChk.IsChecked == true, // 查詢視窗失焦自動隱藏（#複查）
        PassedTransparentChk.IsChecked == true); // 過關卡透明底（#123）

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

    private void OnSave(object sender, RoutedEventArgs e) => TrySave();

    /// <summary>
    /// 執行儲存（#125）：成功回傳 <c>true</c>——**不彈成功模態**，成功回饋改由呼叫端經
    /// <see cref="SettingsChanged"/>→App→主視窗狀態列輕量閃示「Saved ✓」；失敗回傳 <c>false</c>
    /// 並以**持續性**模態報錯（非數秒閃示、免一閃即逝）、留在頁上。供「儲存」鈕與「存後離開」守衛共用。
    /// </summary>
    public bool TrySave()
    {
        try
        {
            ApplyKeyIfProvided();
            Config = Gather();
            Config.Save(AppConfig.SettingsPath); // %APPDATA%（Issue #51 遷居；exe 旁不再寫）
            KeyBox.Clear();
            SetConfig(Config);
            SettingsChanged?.Invoke(Config); // 成功回饋走狀態列閃示（#125，取代原「Saved.」模態框）
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Save failed: " + ex.Message, "ScreenTrans Options");
            return false;
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
