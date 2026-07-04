using System.Windows;
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

        SaveBtn.Click += OnSave;
        CancelBtn.Click += (_, _) => DialogResult = false;
        TestBtn.Click += OnTest;
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
        ResultConfig.TimeoutSec,
        TagOf(VoiceBox));

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
