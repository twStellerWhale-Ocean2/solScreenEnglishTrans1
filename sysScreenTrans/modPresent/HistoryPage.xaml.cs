using ScreenTrans.Query;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using Thickness = System.Windows.Thickness;
using CornerRadius = System.Windows.CornerRadius;
using Visibility = System.Windows.Visibility;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using FontWeights = System.Windows.FontWeights;
using TextTrimming = System.Windows.TextTrimming;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;
using UIElement = System.Windows.UIElement;
using Cursors = System.Windows.Input.Cursors;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace ScreenTrans.Present;

/// <summary>
/// 查詢歷史分頁（Issue #34；原 HistoryWindow 內容移入為 UserControl）：左依日期分組、右該日條目
/// （前端「＋筆記」加入我的筆記、尾端 播音／檢視／刪除、頂部清除全部）。非結果視窗、由主視窗分頁承載。
/// </summary>
public partial class HistoryPage : UserControl
{
    private readonly HistoryStore _store;
    private readonly Func<ISpeechService?> _speech;

    public event Action<HistoryEntry>? ViewRequested;
    public event Action<HistoryEntry>? AddToNotesRequested;

    /// <summary>目前檢視（選取日期）之條目數（#132，供狀態列顯示）。</summary>
    public int CurrentEntryCount { get; private set; }

    /// <summary>目前檢視條目數變更（#132）：切日/清除/重繪後觸發。</summary>
    public event Action<int>? EntryCountChanged;

    private sealed record DateGroup(DateTime Date, List<HistoryEntry> Entries);

    public HistoryPage(HistoryStore store, Func<ISpeechService?> speechProvider)
    {
        InitializeComponent();
        _store = store;
        _speech = speechProvider;
        ClearAllBtn.Click += OnClearAll;
        Reload();
    }

    public void Reload()
    {
        DateTime? keep = (DateList.SelectedItem as ListBoxItem)?.Tag is DateGroup sel ? sel.Date : null;

        var groups = _store.Load()
            .GroupBy(e => e.Timestamp.ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new DateGroup(g.Key, g.ToList()))
            .ToList();

        DateList.SelectionChanged -= OnDateChanged;
        DateList.Items.Clear();
        foreach (var g in groups)
        {
            DateList.Items.Add(new ListBoxItem { Content = DateItem(g), Tag = g, Padding = new Thickness(4) });
        }
        DateList.SelectionChanged += OnDateChanged;

        bool any = groups.Count > 0;
        EmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClearAllBtn.IsEnabled = any;
        if (!any)
        {
            EntryPanel.Children.Clear();
            CurrentEntryCount = 0;
            EntryCountChanged?.Invoke(0); // #132
            return;
        }
        int idx = keep is null ? 0 : Math.Max(0, groups.FindIndex(g => g.Date == keep));
        DateList.SelectedIndex = idx;
    }

    private void OnDateChanged(object? sender, SelectionChangedEventArgs e) => RenderSelected();

    // 單擊選取（Issue #110，同筆記款；共用 CardSelector）：單選、僅視覺回饋；框厚恆定 2px 只換色。
    private readonly CardSelector _selector = new();

    private void RenderSelected()
    {
        _selector.Clear(); // 重繪/切日即清選取（#110）
        EntryPanel.Children.Clear();
        if ((DateList.SelectedItem as ListBoxItem)?.Tag is not DateGroup g)
        {
            CurrentEntryCount = 0;
            EntryCountChanged?.Invoke(0); // #132
            return;
        }
        foreach (var entry in g.Entries)
        {
            EntryPanel.Children.Add(EntryRow(entry));
        }
        CurrentEntryCount = g.Entries.Count; // #132
        EntryCountChanged?.Invoke(CurrentEntryCount);
    }

    private void OnClearAll(object? sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Clear all query history? This cannot be undone.", "Clear All",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        _store.Clear();
        Reload();
    }

    private static StackPanel DateItem(DateGroup g)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = g.Date.ToString("yyyy-MM-dd"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#3A2C33"),
        });
        sp.Children.Add(new TextBlock { Text = $"{g.Entries.Count} item" + (g.Entries.Count == 1 ? "" : "s"), FontSize = 11, Foreground = Brush("#8A5A6D") });
        return sp;
    }

    // 條目卡（Issue #77）：改比照筆記機制——單行原文＋時刻、行尾播音鈕、右鍵選單（播音/檢視/加入筆記/刪除）、
    // 雙擊＝檢視；歷史不排序/不移動故無拖曳握把、無底色。
    private UIElement EntryRow(HistoryEntry entry)
    {
        var card = new Border
        {
            Background = NoteCardBrush.For(null, EntryDisplaySettings.CardOpacity), // v1.0.1（USR 回饋）：底＝白×可調透明度（與筆記頁共用同一設定值）
            BorderBrush = Brush(CardSelector.IdleBorder),
            BorderThickness = new Thickness(2), // #110：框厚恆定 2px（選取只換色不跳版）
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 3, 10, 3), // #複查：內距縮小、免浪費空間（原 6 上下）
            Margin = new Thickness(0, 0, 0, 5),
            Focusable = true,        // v1.0.1（USR 回饋）：選取後可接收 Delete 鍵刪除本條目
            FocusVisualStyle = null, // 選取已以框色表示，免預設虛線焦點框
        };
        card.MouseRightButtonDown += (_, _) => { _selector.Select(card); card.Focus(); }; // 右鍵亦設選取（#110）＋取焦點
        // v1.0.1（USR 回饋）：選取後按 Delete 直接刪除本條目（比照右鍵選單 Delete、與其一致不另確認）
        card.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Delete)
            {
                _store.Delete(entry.Id);
                Reload();
                e.Handled = true;
            }
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 原文
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 時刻
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 行尾播音鈕

        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Original) ? "(No English text detected)" : entry.Original,
            FontSize = EntryDisplaySettings.FontSize, // #複查：選項頁「條目顯示」可調字級/粗體/換行
            FontWeight = EntryDisplaySettings.Bold ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal,
            Foreground = Brush("#3A2C33"),
            TextWrapping = EntryDisplaySettings.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = EntryDisplaySettings.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        // #123：兩頁統一——改用共用 EntryTimeCell（兩行 date/time、日期縮小），與筆記條目同款
        var time = EntryTimeCell.Build(entry.Timestamp.ToLocalTime());
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);

        // 行尾播音鈕（比照筆記 Issue #56）：單擊播音、不冒泡至卡片（不觸發雙擊檢視）
        var playBtn = new Button
        {
            Content = "", // Segoe MDL2 Assets：Play
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brush("#2F6FED"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Play",
        };
        playBtn.Click += (_, _) => _speech()?.Speak(entry.Original, "en-US", stopPrevious: true);
        Grid.SetColumn(playBtn, 2);
        grid.Children.Add(playBtn);

        card.Child = grid;
        card.Tag = entry;
        card.ContextMenu = MakeEntryMenu(entry, card);
        card.MouseLeftButtonDown += (_, e) =>
        {
            _selector.Select(card); // 單擊即選取（#110）
            card.Focus();           // v1.0.1：取鍵盤焦點，使 Delete 鍵路由至本卡（刪除本條目）
            if (e.ClickCount == 2) // 雙擊＝檢視（比照筆記）
            {
                ViewRequested?.Invoke(entry);
                e.Handled = true;
            }
        };
        return card;
    }

    // 右鍵選單（Issue #77，比照筆記；差異：以「加入筆記」取代筆記之「底色」、無移動）
    private ContextMenu MakeEntryMenu(HistoryEntry entry, Border card)
    {
        var menu = new ContextMenu();
        var play = new MenuItem { Header = "▶ Play", Foreground = Brush("#2F6FED") };
        play.Click += (_, _) => _speech()?.Speak(entry.Original, "en-US", stopPrevious: true);
        var view = new MenuItem { Header = "View" };
        view.Click += (_, _) => ViewRequested?.Invoke(entry);
        var edit = new MenuItem { Header = "Edit text" }; // 複查回饋：校正原文→自動重譯更新中文
        // 直接捕捉 card；MenuItem.Parent 對 ContextMenu 頂層項常回 null，不可靠。
        edit.Click += (_, _) => BeginEntryEdit(card, entry);
        var addNote = new MenuItem { Header = "Add to Notes", Foreground = Brush("#2F6F4A") };
        addNote.Click += (_, _) => AddToNotesRequested?.Invoke(entry);
        var delete = new MenuItem { Header = "Delete", Foreground = Brush("#B23B3B") };
        delete.Click += (_, _) => { _store.Delete(entry.Id); Reload(); };
        menu.Items.Add(play);
        menu.Items.Add(view);
        menu.Items.Add(edit);
        menu.Items.Add(addNote);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>編輯歷史條目原文後自動重譯（複查回饋）：Save 交 App 重查更新、Cancel 還原。</summary>
    public event Action<string, string>? EntryEditRequested;

    private void BeginEntryEdit(Border card, HistoryEntry entry)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = entry.Original,
            FontSize = EntryDisplaySettings.FontSize,
            AcceptsReturn = true,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Padding = new Thickness(4),
        };
        var save = new Button
        {
            Content = "Save & re-translate", Padding = new Thickness(10, 4, 10, 4),
            Background = Brush("#F4C2D0"), Foreground = Brush("#6D3A4D"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        save.Click += (_, _) => EntryEditRequested?.Invoke(entry.Id, box.Text);
        var cancel = new Button
        {
            Content = "Cancel", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4),
            Background = Brush("#66FFFFFF"), Foreground = Brush("#6D3A4D"),
            BorderBrush = Brush("#E4B7C6"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        cancel.Click += (_, _) => RenderSelected();
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(save);
        row.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(row);
        card.Child = panel;
        box.Focus();
        box.SelectAll();
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
