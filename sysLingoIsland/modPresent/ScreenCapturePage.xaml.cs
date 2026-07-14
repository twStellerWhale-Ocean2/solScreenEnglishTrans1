using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LingoIsland.Capture;
using LingoIsland.Query;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using Thickness = System.Windows.Thickness;
using Visibility = System.Windows.Visibility;
using VerticalAlignment = System.Windows.VerticalAlignment;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace LingoIsland.Present;

/// <summary>
/// 螢幕截圖分頁（epic #145 增量2 拆出，增量3 加截圖管理）：上為擷取控制（Capture Screen＋喚起快捷鍵狀態/變更），
/// 下為 <b>截圖管理</b>——擷取之截圖保存於 <see cref="ScreenshotStore"/>，於此縮圖清單檢視／刪除／清空（標記擷取當下使用中主題）。
/// 事件供 App 接：<see cref="CaptureRequested"/>／<see cref="HotkeyChanged"/>／<see cref="ListeningChanged"/>。
/// </summary>
public partial class ScreenCapturePage : UserControl
{
    // 喚起快捷鍵設定（#133；保留 #127 視窗層擷取寫法）
    private HotKeyBinding _hotkey = HotKeyBinding.Default;
    private bool _listening;
    private System.Windows.Window? _listenWindow;

    // 截圖管理（epic #145 增量3）
    private readonly ScreenshotStore _shots;
    private readonly ThemeStore _themes;      // 依 theme 篩選（多媒體主題管理·B）
    private bool _populatingFilter;           // 重填篩選下拉期間抑制 SelectionChanged→重整
    private string? _selectedShotId;

    /// <summary>手動觸發螢幕擷取（#5：「Capture Screen」鈕）；呼叫端收合主視窗後走既有喚起主動線。</summary>
    public event Action? CaptureRequested;

    /// <summary>監聽指定快捷鍵之開始（<c>true</c>）／結束（<c>false</c>）；呼叫端據此暫停／恢復全域熱鍵（Issue #89）。</summary>
    public event Action<bool>? ListeningChanged;

    /// <summary>擷取到新綁定時觸發（#133）；呼叫端據此持久化＋重註冊全域熱鍵。</summary>
    public event Action<HotKeyBinding>? HotkeyChanged;

    public ScreenCapturePage(string initialHotkey, ScreenshotStore shots, ThemeStore themes)
    {
        InitializeComponent();
        _shots = shots;
        _themes = themes;

        ChangeHotkeyBtn.Click += (_, _) => StartListening();
        CaptureScreenBtn.Click += (_, _) => CaptureRequested?.Invoke();
        Unloaded += (_, _) => StopListening();

        // 截圖管理＋依 theme 篩選（B）
        ShotList.SelectionChanged += OnShotSelect;
        DeleteShotBtn.Click += OnDeleteShot;
        ClearShotsBtn.Click += OnClearShots;
        ShotThemeFilter.SelectionChanged += (_, _) => { if (!_populatingFilter) { RefreshScreenshots(); } };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { PopulateThemeFilter(); RefreshScreenshots(); } }; // 切回本頁重填（反映主題增刪改）

        _hotkey = HotKeyBinding.Parse(initialHotkey); // 目前快捷鍵初值（自 AppConfig.Hotkey）
        UpdateHotkeyStatus();
        PopulateThemeFilter();
        RefreshScreenshots();
    }

    /// <summary>以目前主題重填「依 theme 篩選」下拉（圖文）；期間抑制重整、保留選取。</summary>
    private void PopulateThemeFilter()
    {
        _populatingFilter = true;
        ThemeFilter.Populate(ShotThemeFilter, _themes);
        _populatingFilter = false;
    }

    // ---- 截圖管理（epic #145 增量3） ----

    /// <summary>重載截圖清單（縮圖＋擷取時間＋主題名）；App 於每次擷取保存後呼叫。空清單顯提示。</summary>
    public void RefreshScreenshots()
    {
        var d = _shots.Load();
        var themeId = ThemeFilter.SelectedThemeId(ShotThemeFilter); // null＝All（B）
        var shown = d.Items.Where(it => ThemeFilter.Match(themeId, it.ThemeId)).ToList();
        ShotList.SelectionChanged -= OnShotSelect;
        ShotList.Items.Clear();
        foreach (var it in shown)
        {
            ShotList.Items.Add(new ListBoxItem { Content = ShotItemView(it), Tag = it, Padding = new Thickness(4) });
        }
        ShotList.SelectionChanged += OnShotSelect;

        var any = shown.Count > 0;
        ShotEmptyHint.Text = d.Items.Count == 0
            ? "No screenshots yet. Capture a screen (button or hotkey) to save it here."
            : "No screenshots for this theme."; // 有截圖但本 theme 無
        ShotEmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
        {
            ShotPreview.Source = null;
            DeleteShotBtn.IsEnabled = false;
            _selectedShotId = null;
        }
    }

    private StackPanel ShotItemView(ScreenshotItem it)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var thumb = new Image { Width = 56, Height = 36, Stretch = System.Windows.Media.Stretch.UniformToFill, Margin = new Thickness(0, 0, 8, 0) };
        var src = LoadImage(_shots.ImagePathFor(it.File));
        if (src is not null) { thumb.Source = src; }
        sp.Children.Add(thumb);

        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(new TextBlock { Text = FormatTime(it.CapturedAt), FontSize = 12, Foreground = Brush("#3A2C33") });
        if (!string.IsNullOrWhiteSpace(it.ThemeName))
        {
            col.Children.Add(new TextBlock { Text = it.ThemeName, FontSize = 11, Foreground = Brush("#9A6A82") });
        }
        sp.Children.Add(col);
        return sp;
    }

    private static string FormatTime(string iso) =>
        DateTimeOffset.TryParse(iso, out var t) ? t.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : iso;

    private void OnShotSelect(object? sender, SelectionChangedEventArgs e)
    {
        var it = (ShotList.SelectedItem as ListBoxItem)?.Tag as ScreenshotItem;
        _selectedShotId = it?.Id;
        ShotPreview.Source = it is not null ? LoadImage(_shots.ImagePathFor(it.File)) : null;
        DeleteShotBtn.IsEnabled = it is not null;
    }

    private void OnDeleteShot(object? sender, RoutedEventArgs e)
    {
        if (_selectedShotId is null) { return; }
        _shots.Remove(_selectedShotId);
        _selectedShotId = null;
        RefreshScreenshots();
    }

    private void OnClearShots(object? sender, RoutedEventArgs e)
    {
        if (ShotList.Items.Count == 0) { return; }
        if (MessageBox.Show("Delete all captured screenshots?", "Clear screenshots",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        _shots.Clear();
        RefreshScreenshots();
    }

    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path)) { return null; }
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // ---- 喚起快捷鍵監聽（#133；保留 #127 視窗層擷取寫法、不依賴本頁鍵盤焦點） ----

    private void UpdateHotkeyStatus()
    {
        HotkeyStatus.Text = _hotkey.DisplayName; // 標籤已為「Hotkey:」，此處僅顯示值（#165）
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
        HotkeyChanged?.Invoke(binding);
        StopListening();
    }

    private static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or
        Key.System or Key.None;
}
