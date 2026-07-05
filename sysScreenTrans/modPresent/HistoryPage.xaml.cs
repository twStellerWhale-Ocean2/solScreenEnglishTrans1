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
            return;
        }
        int idx = keep is null ? 0 : Math.Max(0, groups.FindIndex(g => g.Date == keep));
        DateList.SelectedIndex = idx;
    }

    private void OnDateChanged(object? sender, SelectionChangedEventArgs e) => RenderSelected();

    private void RenderSelected()
    {
        EntryPanel.Children.Clear();
        if ((DateList.SelectedItem as ListBoxItem)?.Tag is not DateGroup g)
        {
            return;
        }
        foreach (var entry in g.Entries)
        {
            EntryPanel.Children.Add(EntryRow(entry));
        }
    }

    private void OnClearAll(object? sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("確定清除全部查詢歷史？此動作無法復原。", "清除全部",
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
        sp.Children.Add(new TextBlock { Text = $"{g.Entries.Count} 筆", FontSize = 11, Foreground = Brush("#8A5A6D") });
        return sp;
    }

    // 條目卡（Issue #77）：改比照筆記機制——單行原文＋時刻、行尾播音鈕、右鍵選單（播音/檢視/加入筆記/刪除）、
    // 雙擊＝檢視；歷史不排序/不移動故無拖曳握把、無底色。
    private UIElement EntryRow(HistoryEntry entry)
    {
        var card = new Border
        {
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#F4C2D0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 5),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 原文
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 時刻
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 行尾播音鈕

        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Original) ? "（未偵測到英文文字）" : entry.Original,
            FontSize = 13.5,
            Foreground = Brush("#3A2C33"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var time = new TextBlock
        {
            Text = entry.Timestamp.ToLocalTime().ToString("HH:mm"),
            FontSize = 11,
            Foreground = Brush("#9A6A82"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
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
            ToolTip = "播音",
        };
        playBtn.Click += (_, _) => _speech()?.Speak(entry.Original, "en-US", stopPrevious: true);
        Grid.SetColumn(playBtn, 2);
        grid.Children.Add(playBtn);

        card.Child = grid;
        card.Tag = entry;
        card.ContextMenu = MakeEntryMenu(entry);
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) // 雙擊＝檢視（比照筆記）
            {
                ViewRequested?.Invoke(entry);
                e.Handled = true;
            }
        };
        return card;
    }

    // 右鍵選單（Issue #77，比照筆記；差異：以「加入筆記」取代筆記之「底色」、無移動）
    private ContextMenu MakeEntryMenu(HistoryEntry entry)
    {
        var menu = new ContextMenu();
        var play = new MenuItem { Header = "▶ 播音", Foreground = Brush("#2F6FED") };
        play.Click += (_, _) => _speech()?.Speak(entry.Original, "en-US", stopPrevious: true);
        var view = new MenuItem { Header = "檢視" };
        view.Click += (_, _) => ViewRequested?.Invoke(entry);
        var addNote = new MenuItem { Header = "加入筆記", Foreground = Brush("#2F6F4A") };
        addNote.Click += (_, _) => AddToNotesRequested?.Invoke(entry);
        var delete = new MenuItem { Header = "刪除", Foreground = Brush("#B23B3B") };
        delete.Click += (_, _) => { _store.Delete(entry.Id); Reload(); };
        menu.Items.Add(play);
        menu.Items.Add(view);
        menu.Items.Add(addNote);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
