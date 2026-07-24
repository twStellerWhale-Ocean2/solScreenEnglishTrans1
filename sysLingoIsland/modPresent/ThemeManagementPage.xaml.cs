using System.IO;
using System.Windows.Media.Imaging;
using LingoIsland.Query;
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
using Key = System.Windows.Input.Key;

namespace LingoIsland.Present;

/// <summary>
/// 多媒體主題管理分頁（Issue #36，spec#9；epic #145 增量2：自 Capture 頁獨立為專屬頁籤）：左命名主題清單
/// （名稱＋縮圖＋使用中標記），右編輯區（名稱、描述、貼上剪貼簿圖片／上傳檔、以 vision 自動解釋、設為使用中、刪除、儲存）。
/// 查詢注入來源＝使用中主題描述。螢幕擷取與喚起快捷鍵已移至 <see cref="ScreenCapturePage"/>。
/// </summary>
public partial class ThemeManagementPage : UserControl
{
    private readonly ThemeStore _store;
    private readonly Func<byte[], Task<ImageContext>> _describe;
    private ThemesData _data = new();
    private ThemeItem? _selected;
    private byte[]? _pending; // 剛貼上/上傳、尚未儲存之圖片
    private readonly List<(Border Swatch, TextBox Box)> _colorRows = new(); // 12 色可編輯列（#189-checklist USR）：色票（點擊改色）＋描述框

    public ThemeManagementPage(ThemeStore store, Func<byte[], Task<ImageContext>> describeAsync)
    {
        InitializeComponent();
        _store = store;
        _describe = describeAsync;

        AddBtn.Click += OnAdd;
        SaveBtn.Click += OnSave;
        ActiveBtn.Click += OnSetActive;
        PasteBtn.Click += OnPaste;
        UploadBtn.Click += OnUpload;
        DescribeBtn.Click += OnDescribe;
        List.SelectionChanged += OnSelect;
        // 刪除主題改右鍵選單/Delete 鍵（#169，取代編輯區 Delete 按鈕，統一版面）
        List.ContextMenu = ListDeleteSupport.DeleteMenu(DeleteSelectedTheme);
        List.PreviewMouseRightButtonDown += ListDeleteSupport.SelectItemUnderMouse;
        List.KeyDown += (_, e) => { if (e.Key == Key.Delete) { DeleteSelectedTheme(); } };

        // 圖片卡拖放圖片檔（Issue #69）
        ImageDropCard.DragOver += (_, e) => { e.Effects = HasImageFile(e) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        ImageDropCard.Drop += OnImageDrop;

        BuildColorRows(); // 12 色可編輯列（#189-checklist USR）
        Reload();
    }

    // ---- 主題 12 色可編輯色盤（#189-checklist USR：色票點擊改色＋描述、無名稱） ----

    private void BuildColorRows()
    {
        ColorRulesPanel.Children.Clear();
        _colorRows.Clear();
        for (var i = 0; i < ThemeColors.Count; i++)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 色票
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 描述

            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                Background = Brush(ThemeColors.HexagonDefaults[i]),
                BorderBrush = Brush("#D8B4C2"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "點按即可更改此顏色",
            };
            swatch.MouseLeftButtonUp += OnSwatchClick; // 點色票→取色器改色
            Grid.SetColumn(swatch, 0);
            grid.Children.Add(swatch);

            var box = new TextBox
            {
                FontSize = 12.5,
                Padding = new Thickness(5, 3, 5, 3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "說明哪些說話人／字幕行使用此顏色——例：角色名稱；留空＝未使用。",
            };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            _colorRows.Add((swatch, box));
            ColorRulesPanel.Children.Add(grid);
        }
    }

    /// <summary>點色票→系統取色器改該槽顏色（#189-checklist USR「可點選改色」）；按 Save 才寫回主題。</summary>
    private void OnSwatchClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border sw) { return; }
        var cur = (sw.Background as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Gray;
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B),
            FullOpen = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            sw.Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        }
    }

    /// <summary>色票目前色→<c>#RRGGBB</c>（存檔用）。</summary>
    private static string SwatchHex(Border sw, string fallback)
        => sw.Background is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : fallback;

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
            if (src is null) { MessageBox.Show("無法讀取圖片。", "拖曳圖片"); return; }
            _pending = ToPng(src);
            ShowPreview(FromBytes(_pending));
            StatusLine.Text = "已拖入圖片。可「從圖片自動解釋」或直接「儲存」。";
        }
        catch (Exception ex)
        {
            MessageBox.Show("讀取失敗：" + ex.Message, "拖曳圖片");
        }
    }

    public void Reload() => Reload(preferActive: false);

    /// <summary>
    /// 重載主題清單。<paramref name="preferActive"/>＝true（切到本頁時，USR）：忽略上次選取、**預設選使用中主題**；
    /// 否則（頁內操作後）保留原選取。無選取一律退回：使用中→（無使用中則）首則。
    /// </summary>
    public void Reload(bool preferActive)
    {
        var keepId = preferActive ? null : _selected?.Id;
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
        SelectById(keepId ?? ThemeStore.GetActive(_data)?.Id ?? _data.Items[0].Id);
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

    private StackPanel ItemView(ThemeItem it)
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
            col.Children.Add(new TextBlock { Text = "● 使用中", FontSize = 11, Foreground = Brush("#2E7D46") });
        }
        sp.Children.Add(col);
        return sp;
    }

    private void SelectById(string id)
    {
        for (int i = 0; i < List.Items.Count; i++)
        {
            if ((List.Items[i] as ListBoxItem)?.Tag is ThemeItem it && it.Id == id)
            {
                List.SelectedIndex = i;
                return;
            }
        }
        if (List.Items.Count > 0) { List.SelectedIndex = 0; }
    }

    private void OnSelect(object? sender, SelectionChangedEventArgs e)
    {
        _selected = (List.SelectedItem as ListBoxItem)?.Tag as ThemeItem;
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
        BlockBox.Text = _selected.BlockedWords; // #217：自動屏蔽字串
        StatusLine.Text = _selected.IsActive ? "此主題為使用中。" : "";
        ShowPreview(!string.IsNullOrEmpty(_selected.Image) ? LoadImage(_store.ImagePathFor(_selected.Image!)) : null);
        // 載入 12 色（色票色＋描述，#189-checklist）
        ThemeColors.Ensure(_selected);
        for (var i = 0; i < _colorRows.Count && i < _selected.Colors.Count; i++)
        {
            var col = _selected.Colors[i];
            _colorRows[i].Swatch.Background = Brush(string.IsNullOrWhiteSpace(col.Hex) ? ThemeColors.HexagonDefaults[i] : col.Hex);
            _colorRows[i].Box.Text = col.Description;
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
        var created = ThemeStore.Add(_data, ThemeStore.DefaultName);
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
        ThemeStore.Rename(_data, _selected.Id, NameBox.Text);
        ThemeStore.UpdateText(_data, _selected.Id, DescBox.Text);
        _selected.BlockedWords = BlockBox.Text?.Trim() ?? ""; // #217：自動屏蔽字串（與 Colors 同採直接指派）
        if (_pending is not null)
        {
            _selected.Image = _store.WriteImage(_selected.Id, _pending);
            _pending = null;
        }
        // 收集 12 色（色票 hex＋描述，#189-checklist）：恆 12 槽、描述空白亦保留（色票仍可能被別處引用）
        _selected.Colors = new List<ThemeColor>();
        for (var i = 0; i < _colorRows.Count; i++)
        {
            _selected.Colors.Add(new ThemeColor
            {
                Hex = SwatchHex(_colorRows[i].Swatch, ThemeColors.HexagonDefaults[i]),
                Description = _colorRows[i].Box.Text?.Trim() ?? "",
            });
        }
        _store.Save(_data);
        BuildList();
        SelectById(_selected.Id);
        StatusLine.Text = "已儲存。";
    }

    private void OnSetActive(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        ThemeStore.SetActive(_data, _selected.Id);
        _store.Save(_data);
        BuildList();
        SelectById(_selected.Id);
        StatusLine.Text = "已設為使用中；查詢將使用此主題。";
    }

    /// <summary>刪除選取主題（#169：清單右鍵選單「Delete」或按 Delete 鍵；含確認）。</summary>
    private void DeleteSelectedTheme()
    {
        if (_selected is null) { return; }
        if (MessageBox.Show($"刪除主題「{_selected.Name}」？", "刪除主題",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        var removed = ThemeStore.Remove(_data, _selected.Id);
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
            MessageBox.Show("剪貼簿上沒有圖片。", "貼上圖片");
            return;
        }
        _pending = ToPng(img);
        ShowPreview(FromBytes(_pending));
        StatusLine.Text = "已貼上圖片。可「從圖片自動解釋」或直接「儲存」。";
    }

    private void OnUpload(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        var dlg = new OpenFileDialog { Filter = "圖片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有檔案|*.*" };
        if (dlg.ShowDialog() != true) { return; }
        try
        {
            var src = LoadImage(dlg.FileName);
            if (src is null) { MessageBox.Show("無法讀取圖片。", "上傳檔案"); return; }
            _pending = ToPng(src);
            ShowPreview(FromBytes(_pending));
            StatusLine.Text = "已載入圖片。可「從圖片自動解釋」或直接「儲存」。";
        }
        catch (Exception ex)
        {
            MessageBox.Show("讀取失敗：" + ex.Message, "上傳檔案");
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
            MessageBox.Show("請先貼上或上傳圖片。", "從圖片自動解釋");
            return;
        }
        DescribeBtn.IsEnabled = false;
        StatusLine.Text = "AI 分析中…";
        try
        {
            var result = await _describe(bytes);
            var desc = result.Description;
            DescBox.Text = string.IsNullOrWhiteSpace(DescBox.Text) ? desc : DescBox.Text.TrimEnd() + "\n" + desc;
            // #53：名稱尚未填（空白或仍為預設佔位）且可辨識出作品名 → 自動填入；已鍵入實際名稱則不覆寫
            var filledName = false;
            if (ThemeStore.ShouldAutoFillName(NameBox.Text) && !string.IsNullOrWhiteSpace(result.Name))
            {
                NameBox.Text = result.Name.Trim();
                filledName = true;
            }
            StatusLine.Text = filledName
                ? $"已辨識「{result.Name.Trim()}」並填入名稱與描述。可視需要編輯，再「儲存」。"
                : "已產生描述。可視需要編輯，再「儲存」。";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "";
            MessageBox.Show("圖片分析失敗：" + ex.Message, "從圖片自動解釋");
        }
        finally
        {
            DescribeBtn.IsEnabled = true;
        }
    }

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
