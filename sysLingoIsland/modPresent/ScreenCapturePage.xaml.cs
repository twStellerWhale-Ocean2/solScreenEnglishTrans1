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
    private readonly ThemeStore _themes;      // 依 theme 篩選（多媒體主題管理·B）＋內容區塊所屬主題指派（#173）
    private bool _populatingFilter;           // 重填篩選下拉期間抑制 SelectionChanged→重整
    private bool _populatingShotPicker;       // 重填「所屬主題」下拉期間抑制 SelectionChanged→重指派（#173）
    private string? _selectedShotId;
    private int _lastShotCount = -1;           // 偵測新擷取（數目增加）→自動切到【內容】檢視（#182）

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
        // 子頁籤（#182 版面統一）：獲得（擷取控制）／內容（清單＋預覽），以可見性切換
        ShotTabAcquire.Checked += (_, _) => ShowShotTab(acquire: true);
        ShotTabContent.Checked += (_, _) => ShowShotTab(acquire: false);

        // 截圖管理＋依 theme 篩選（B）；刪除改右鍵選單/Delete 鍵（#167，取代 Delete 按鈕）
        ShotList.SelectionChanged += OnShotSelect;
        ClearShotsBtn.Click += OnClearShots;
        ShotThemeFilter.SelectionChanged += (_, _) => { if (!_populatingFilter) { RefreshScreenshots(); } };
        ShotThemePicker.SelectionChanged += (_, _) => { if (!_populatingShotPicker) { OnShotThemePicked(); } }; // 內容區塊改指派所屬主題（#173）
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { PopulateThemeFilter(); RefreshScreenshots(); } }; // 切回本頁重填（反映主題增刪改）
        ShotList.ContextMenu = ListDeleteSupport.DeleteMenu(DeleteSelectedShot);
        ShotList.PreviewMouseRightButtonDown += ListDeleteSupport.SelectItemUnderMouse;
        ShotList.KeyDown += (_, e) => { if (e.Key == Key.Delete) { DeleteSelectedShot(); } };

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
            _selectedShotId = null;
        }
        UpdateShotThemePicker(); // 選取於重整後被清空 → 停用/重填「所屬主題」下拉，與清單同步（#173）
        if (_lastShotCount >= 0 && d.Items.Count > _lastShotCount) { ShotTabContent.IsChecked = true; } // 新擷取→切到【內容】檢視（#182）
        _lastShotCount = d.Items.Count;
    }

    /// <summary>切換子頁籤（#182）：獲得（擷取控制）／內容（清單＋預覽），以可見性切換。</summary>
    private void ShowShotTab(bool acquire)
    {
        ShotAcquirePane.Visibility = acquire ? Visibility.Visible : Visibility.Collapsed;
        ShotContentPane.Visibility = acquire ? Visibility.Collapsed : Visibility.Visible;
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
        UpdateShotThemePicker(); // 反映選取截圖之所屬主題（#173）
    }

    // ---- 內容區塊「所屬主題」下拉（#173）：顯示選取截圖之主題、改選即重指派 ----

    /// <summary>以選取截圖之所屬主題重填「所屬主題」下拉並啟用；無選取則清空並停用。期間抑制 SelectionChanged→重指派。</summary>
    private void UpdateShotThemePicker()
    {
        var it = (ShotList.SelectedItem as ListBoxItem)?.Tag as ScreenshotItem;
        _populatingShotPicker = true;
        if (it is null)
        {
            ShotThemePicker.Items.Clear();
            ShotThemePicker.IsEnabled = false;
        }
        else
        {
            ThemeFilter.PopulatePicker(ShotThemePicker, _themes, it.ThemeId);
            ShotThemePicker.IsEnabled = true;
        }
        _populatingShotPicker = false;
    }

    /// <summary>「所屬主題」改選→回寫選取截圖之主題（名稱取自現行主題清單）；重整清單並保持選取（落選被篩選則清預覽）。</summary>
    private void OnShotThemePicked()
    {
        var it = (ShotList.SelectedItem as ListBoxItem)?.Tag as ScreenshotItem;
        if (it is null) { return; }
        var id = ThemeFilter.PickedThemeId(ShotThemePicker);
        var name = id is null ? null : ThemeStore.Find(_themes.Load(), id)?.Name;
        _shots.UpdateTheme(it.Id, id, name);
        var keepId = it.Id;
        RefreshScreenshots();  // 反映清單主題名／依 theme 篩選
        ReselectShot(keepId);  // 保持選取，避免預覽消失（被篩選濾掉則清空）
    }

    /// <summary>依 id 重新選中截圖（觸發 OnShotSelect 更新預覽＋主題下拉）；被篩選濾掉則清預覽與下拉。</summary>
    private void ReselectShot(string id)
    {
        for (int i = 0; i < ShotList.Items.Count; i++)
        {
            if ((ShotList.Items[i] as ListBoxItem)?.Tag is ScreenshotItem s && s.Id == id)
            {
                ShotList.SelectedIndex = i;
                return;
            }
        }
        _selectedShotId = null;
        ShotPreview.Source = null;
        UpdateShotThemePicker();
    }

    /// <summary>刪除選取截圖（#167：右鍵選單「Delete」或按 Delete 鍵觸發）。</summary>
    private void DeleteSelectedShot()
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
