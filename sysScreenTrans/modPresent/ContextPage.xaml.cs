using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ScreenTrans.Capture;
using ScreenTrans.Query;
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
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using Border = System.Windows.Controls.Border;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using CornerRadius = System.Windows.CornerRadius;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;

namespace ScreenTrans.Present;

/// <summary>
/// 應用情境分頁（Issue #36，spec#9）：左命名情境清單（名稱＋縮圖＋使用中標記），右編輯區
/// （名稱、描述、貼上剪貼簿圖片／上傳檔、以 vision 自動解釋、設為使用中、刪除、儲存）。查詢注入來源＝使用中情境描述。
/// </summary>
public partial class ContextPage : UserControl
{
    private readonly ContextStore _store;
    private readonly Func<byte[], Task<ImageContext>> _describe;
    private ContextsData _data = new();
    private ContextItem? _selected;
    private byte[]? _pending; // 剛貼上/上傳、尚未儲存之圖片
    private readonly Dictionary<string, TextBox> _colorBoxes = new(); // 各色描述輸入框（Issue #69）

    // 喚起快捷鍵設定（#133：#3 自選項頁整套搬入；保留 #127 視窗層擷取寫法、不依賴本頁鍵盤焦點）
    private HotKeyBinding _hotkey = HotKeyBinding.Default;
    private bool _listening; // 是否正在監聽擷取喚起快捷鍵
    private System.Windows.Window? _listenWindow; // 監聽期間掛載擷取事件之宿主視窗（#127：改視窗層擷取、不依賴本頁鍵盤焦點）

    /// <summary>手動觸發螢幕擷取（#5：擷取頁「Capture Screen」鈕）；呼叫端收合主視窗後走既有喚起主動線。</summary>
    public event Action? CaptureRequested;

    /// <summary>
    /// 監聽指定快捷鍵之開始（<c>true</c>）／結束（<c>false</c>）；呼叫端（App）據此暫停/恢復全域熱鍵，
    /// 避免監聽期間按下與現行相同之鍵誤觸喚起，並讓鍵盤組合不被 <c>RegisterHotKey</c> 吞鍵（Issue #89）。
    /// </summary>
    public event Action<bool>? ListeningChanged;

    /// <summary>擷取到新綁定時觸發（#133：#3）；呼叫端據此持久化 <c>Hotkey</c>、重註冊全域熱鍵、更新狀態。</summary>
    public event Action<HotKeyBinding>? HotkeyChanged;

    public ContextPage(ContextStore store, Func<byte[], Task<ImageContext>> describeAsync, string initialHotkey)
    {
        InitializeComponent();
        _store = store;
        _describe = describeAsync;

        AddBtn.Click += OnAdd;
        SaveBtn.Click += OnSave;
        ActiveBtn.Click += OnSetActive;
        DeleteBtn.Click += OnDelete;
        PasteBtn.Click += OnPaste;
        UploadBtn.Click += OnUpload;
        DescribeBtn.Click += OnDescribe;
        List.SelectionChanged += OnSelect;

        // 喚起快捷鍵：Change 起手監聽、Capture Screen 手動擷取（#133）；監聽於宿主視窗層掛載（見 StartListening，#127），
        // 監聽中本頁被切離（切分頁致 Unloaded）→ 視同取消，確保全域熱鍵必恢復（Issue #89）。
        ChangeHotkeyBtn.Click += (_, _) => StartListening();
        CaptureScreenBtn.Click += (_, _) => CaptureRequested?.Invoke();
        Unloaded += (_, _) => StopListening();

        // 圖片卡拖放圖片檔（Issue #69）
        ImageDropCard.DragOver += (_, e) => { e.Effects = HasImageFile(e) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        ImageDropCard.Drop += OnImageDrop;

        _hotkey = HotKeyBinding.Parse(initialHotkey); // 目前快捷鍵初值（自 AppConfig.Hotkey）
        UpdateHotkeyStatus();
        BuildColorRuleRows(); // 各色描述輸入列（Issue #69）
        Reload();
    }

    // ---- 配色規則各色一格（Issue #69） ----

    private void BuildColorRuleRows()
    {
        ColorRulesPanel.Children.Clear();
        _colorBoxes.Clear();
        foreach (var (name, hex) in NoteColors.Palette)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 色塊
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 色名
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 描述

            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(3),
                Background = Brush(hex),
                BorderBrush = Brush("#D8B4C2"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetColumn(swatch, 0);
            grid.Children.Add(swatch);

            var label = new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                Foreground = Brush("#6D3A4D"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 40,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var box = new TextBox
            {
                FontSize = 12.5,
                Padding = new Thickness(5, 3, 5, 3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = $"Describe which lines use “{name}”; leave blank to skip this color",
            };
            Grid.SetColumn(box, 2);
            grid.Children.Add(box);

            _colorBoxes[name] = box;
            ColorRulesPanel.Children.Add(grid);
        }
    }

    private static bool HasImageFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop)
        && e.Data.GetData(DataFormats.FileDrop) is string[] files
        && files.Any(f => IsImagePath(f));

    private static bool IsImagePath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";
    }

    private void OnImageDrop(object sender, DragEventArgs e)
    {
        if (_selected is null || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }
        var img = files.FirstOrDefault(IsImagePath);
        if (img is null)
        {
            return;
        }
        try
        {
            var src = LoadImage(img);
            if (src is null) { MessageBox.Show("Couldn’t read the image.", "Drop Image"); return; }
            _pending = ToPng(src);
            ShowPreview(FromBytes(_pending));
            StatusLine.Text = "Image dropped. You can “Auto-explain from Image” or just “Save”.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Read failed: " + ex.Message, "Drop Image");
        }
    }

    public void Reload()
    {
        var keepId = _selected?.Id;
        _data = _store.Load();
        BuildList();
        if (_data.Items.Count == 0)
        {
            _selected = null;
            Editor.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }
        EmptyHint.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        SelectById(keepId ?? _data.Items[0].Id);
    }

    private void BuildList()
    {
        List.SelectionChanged -= OnSelect;
        List.Items.Clear();
        foreach (var it in _data.Items)
        {
            List.Items.Add(new ListBoxItem { Content = ItemView(it), Tag = it, Padding = new Thickness(4) });
        }
        List.SelectionChanged += OnSelect;
    }

    private StackPanel ItemView(ContextItem it)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var thumb = new Image { Width = 40, Height = 30, Stretch = System.Windows.Media.Stretch.UniformToFill, Margin = new Thickness(0, 0, 8, 0) };
        if (!string.IsNullOrEmpty(it.Image))
        {
            var src = LoadImage(_store.ImagePathFor(it.Image!));
            if (src is not null) { thumb.Source = src; }
        }
        sp.Children.Add(thumb);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(new TextBlock { Text = it.Name, FontSize = 13, Foreground = Brush("#3A2C33") });
        if (it.IsActive)
        {
            col.Children.Add(new TextBlock { Text = "● Active", FontSize = 11, Foreground = Brush("#2E7D46") });
        }
        sp.Children.Add(col);
        return sp;
    }

    private void SelectById(string id)
    {
        for (int i = 0; i < List.Items.Count; i++)
        {
            if ((List.Items[i] as ListBoxItem)?.Tag is ContextItem it && it.Id == id)
            {
                List.SelectedIndex = i;
                return;
            }
        }
        if (List.Items.Count > 0) { List.SelectedIndex = 0; }
    }

    private void OnSelect(object? sender, SelectionChangedEventArgs e)
    {
        _selected = (List.SelectedItem as ListBoxItem)?.Tag as ContextItem;
        _pending = null;
        LoadEditor();
    }

    private void LoadEditor()
    {
        if (_selected is null)
        {
            return;
        }
        NameBox.Text = _selected.Name;
        DescBox.Text = _selected.Text;
        StatusLine.Text = _selected.IsActive ? "This context is active." : "";
        ShowPreview(!string.IsNullOrEmpty(_selected.Image) ? LoadImage(_store.ImagePathFor(_selected.Image!)) : null);
        // 載入各色描述（Issue #69）
        foreach (var (name, box) in _colorBoxes)
        {
            box.Text = _selected.ColorRules.TryGetValue(name, out var d) ? d : "";
        }
    }

    private void ShowPreview(BitmapSource? src)
    {
        Preview.Source = src;
        NoImageHint.Visibility = src is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 操作 ----

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var created = ContextStore.Add(_data, ContextStore.DefaultName);
        _store.Save(_data);
        BuildList();
        Editor.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        SelectById(created.Id);
        NameBox.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        ContextStore.Rename(_data, _selected.Id, NameBox.Text);
        ContextStore.UpdateText(_data, _selected.Id, DescBox.Text);
        if (_pending is not null)
        {
            _selected.Image = _store.WriteImage(_selected.Id, _pending);
            _pending = null;
        }
        // 收集各色描述（Issue #69）：空白者不存
        _selected.ColorRules.Clear();
        foreach (var (name, box) in _colorBoxes)
        {
            var d = box.Text?.Trim() ?? "";
            if (d.Length > 0)
            {
                _selected.ColorRules[name] = d;
            }
        }
        _store.Save(_data);
        BuildList();
        SelectById(_selected.Id);
        StatusLine.Text = "Saved.";
    }

    private void OnSetActive(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        ContextStore.SetActive(_data, _selected.Id);
        _store.Save(_data);
        BuildList();
        SelectById(_selected.Id);
        StatusLine.Text = "Set active; queries will use this context.";
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        if (MessageBox.Show($"Delete context “{_selected.Name}”?", "Delete Context",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        var removed = ContextStore.Remove(_data, _selected.Id);
        _store.DeleteImage(removed?.Image);
        _store.Save(_data);
        _selected = null;
        Reload();
    }

    private void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        var img = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        if (img is null)
        {
            MessageBox.Show("No image on the clipboard.", "Paste Image");
            return;
        }
        _pending = ToPng(img);
        ShowPreview(FromBytes(_pending));
        StatusLine.Text = "Image pasted. You can “Auto-explain from Image” or just “Save”.";
    }

    private void OnUpload(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dlg.ShowDialog() != true) { return; }
        try
        {
            var src = LoadImage(dlg.FileName);
            if (src is null) { MessageBox.Show("Couldn’t read the image.", "Upload File"); return; }
            _pending = ToPng(src);
            ShowPreview(FromBytes(_pending));
            StatusLine.Text = "Image loaded. You can “Auto-explain from Image” or just “Save”.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Read failed: " + ex.Message, "Upload File");
        }
    }

    private async void OnDescribe(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        byte[]? bytes = _pending;
        if (bytes is null && !string.IsNullOrEmpty(_selected.Image))
        {
            try { bytes = File.ReadAllBytes(_store.ImagePathFor(_selected.Image!)); } catch { bytes = null; }
        }
        if (bytes is null)
        {
            MessageBox.Show("Please paste or upload an image first.", "Auto-explain from Image");
            return;
        }
        DescribeBtn.IsEnabled = false;
        StatusLine.Text = "AI is analyzing…";
        try
        {
            var result = await _describe(bytes);
            var desc = result.Description;
            DescBox.Text = string.IsNullOrWhiteSpace(DescBox.Text) ? desc : DescBox.Text.TrimEnd() + "\n" + desc;
            // #53：名稱尚未填（空白或仍為預設佔位）且可辨識出作品名 → 自動填入；已鍵入實際名稱則不覆寫
            var filledName = false;
            if (ContextStore.ShouldAutoFillName(NameBox.Text) && !string.IsNullOrWhiteSpace(result.Name))
            {
                NameBox.Text = result.Name.Trim();
                filledName = true;
            }
            StatusLine.Text = filledName
                ? $"Recognized “{result.Name.Trim()}” and filled in name and description. Edit if needed, then “Save”."
                : "Description generated. Edit if needed, then “Save”.";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "";
            MessageBox.Show("Image analysis failed: " + ex.Message, "Auto-explain from Image");
        }
        finally
        {
            DescribeBtn.IsEnabled = true;
        }
    }

    // ---- 喚起快捷鍵監聽（#133：#3 自選項頁整套搬入；保留 #127 視窗層擷取寫法、不依賴本頁鍵盤焦點） ----

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
        // 仍嘗試聚焦本頁（利於事件路由與 Esc），但擷取已不依賴之
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

    // ---- 圖片工具 ----

    private static byte[] ToPng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static BitmapImage FromBytes(byte[] bytes)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(bytes);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path)) { return null; }
            return FromBytes(File.ReadAllBytes(path));
        }
        catch { return null; }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
