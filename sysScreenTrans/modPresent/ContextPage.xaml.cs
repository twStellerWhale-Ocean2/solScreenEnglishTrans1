using System.IO;
using System.Windows.Media.Imaging;
using ScreenTrans.Query;
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

namespace ScreenTrans.Present;

/// <summary>
/// 應用情境分頁（Issue #36，spec#9）：左命名情境清單（名稱＋縮圖＋使用中標記），右編輯區
/// （名稱、描述、貼上剪貼簿圖片／上傳檔、以 vision 自動解釋、設為使用中、刪除、儲存）。查詢注入來源＝使用中情境描述。
/// </summary>
public partial class ContextPage : UserControl
{
    private readonly ContextStore _store;
    private readonly Func<byte[], Task<string>> _describe;
    private ContextsData _data = new();
    private ContextItem? _selected;
    private byte[]? _pending; // 剛貼上/上傳、尚未儲存之圖片

    public ContextPage(ContextStore store, Func<byte[], Task<string>> describeAsync)
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

        Reload();
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
            col.Children.Add(new TextBlock { Text = "● 使用中", FontSize = 11, Foreground = Brush("#2E7D46") });
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
        StatusLine.Text = _selected.IsActive ? "此情境使用中。" : "";
        ShowPreview(!string.IsNullOrEmpty(_selected.Image) ? LoadImage(_store.ImagePathFor(_selected.Image!)) : null);
    }

    private void ShowPreview(BitmapSource? src)
    {
        Preview.Source = src;
        NoImageHint.Visibility = src is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 操作 ----

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var created = ContextStore.Add(_data, "新情境");
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
        _store.Save(_data);
        BuildList();
        SelectById(_selected.Id);
        StatusLine.Text = "已儲存。";
    }

    private void OnSetActive(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        ContextStore.SetActive(_data, _selected.Id);
        _store.Save(_data);
        BuildList();
        SelectById(_selected.Id);
        StatusLine.Text = "已設為使用中；查詢將注入此情境。";
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { return; }
        if (MessageBox.Show($"刪除情境「{_selected.Name}」？", "刪除情境",
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
            MessageBox.Show("剪貼簿沒有圖片。", "貼上圖片");
            return;
        }
        _pending = ToPng(img);
        ShowPreview(FromBytes(_pending));
        StatusLine.Text = "已貼上圖片；可「以圖片自動解釋」或直接「儲存」。";
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
            StatusLine.Text = "已載入圖片；可「以圖片自動解釋」或直接「儲存」。";
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
            MessageBox.Show("請先貼上或上傳圖片。", "以圖片自動解釋");
            return;
        }
        DescribeBtn.IsEnabled = false;
        StatusLine.Text = "AI 解釋中…";
        try
        {
            var desc = await _describe(bytes);
            DescBox.Text = string.IsNullOrWhiteSpace(DescBox.Text) ? desc : DescBox.Text.TrimEnd() + "\n" + desc;
            StatusLine.Text = "已產生描述，可手動補充後「儲存」。";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "";
            MessageBox.Show("圖片解釋失敗：" + ex.Message, "以圖片自動解釋");
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
