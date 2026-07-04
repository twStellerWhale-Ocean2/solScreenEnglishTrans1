using ScreenTrans.Query;
using Window = System.Windows.Window;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using GridLength = System.Windows.GridLength;
using Thickness = System.Windows.Thickness;
using CornerRadius = System.Windows.CornerRadius;
using Visibility = System.Windows.Visibility;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using FontWeights = System.Windows.FontWeights;
using TextTrimming = System.Windows.TextTrimming;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using GridUnitType = System.Windows.GridUnitType;
using Cursors = System.Windows.Input.Cursors;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UIElement = System.Windows.UIElement;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace ScreenTrans.Present;

/// <summary>
/// 查詢歷史檢視視窗（[modPresent模組] 查詢歷史檢視契約，design ＜III.C.(C)＞ 查詢歷史頁，spec#6）：
/// 左側依本地日期分組、右側該日條目垂直堆疊（新在上、最舊在下）；單筆可重聽（英文原句）、
/// 檢視（<see cref="ViewRequested"/> 由呼叫端開結果卡片詳情，重用整句/逐字發音）、刪除；頂部「清除全部」。
/// <b>非結果視窗</b>——獨立開關、不受「同時至多一個結果視窗、下一次查詢取代」約束、不被下一次查詢關閉。
/// 語音以 provider 委派取用（永遠取當前 <see cref="ISpeechService"/>、不持有可能被設定變更釋放的舊實例）。
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryStore _store;
    private readonly Func<ISpeechService?> _speech;

    /// <summary>使用者對某筆按「檢視」時觸發；呼叫端開結果卡片顯示三欄詳情（重用結果視窗）。</summary>
    public event Action<HistoryEntry>? ViewRequested;

    private sealed record DateGroup(DateTime Date, List<HistoryEntry> Entries);

    public HistoryWindow(HistoryStore store, Func<ISpeechService?> speechProvider)
    {
        InitializeComponent();
        _store = store;
        _speech = speechProvider;
        ClearAllBtn.Click += OnClearAll;
        Reload(); // Reload 於重建期間自行 detach/attach OnSelectionChanged
    }

    /// <summary>重讀歷史檔並重建左側日期清單（查詢新增／刪除／清除後呼叫）；盡量沿用原選取日期。</summary>
    public void Reload()
    {
        DateTime? keep = (DateList.SelectedItem as ListBoxItem)?.Tag is DateGroup sel ? sel.Date : null;

        var groups = _store.Load()
            .GroupBy(e => e.Timestamp.ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new DateGroup(g.Key, g.ToList())) // Load 已新在前，群內順序沿用
            .ToList();

        DateList.SelectionChanged -= OnSelectionChanged; // 重建期間不觸發渲染
        DateList.Items.Clear();
        foreach (var g in groups)
        {
            DateList.Items.Add(new ListBoxItem { Content = DateItem(g), Tag = g, Padding = new Thickness(4) });
        }
        DateList.SelectionChanged += OnSelectionChanged;

        bool any = groups.Count > 0;
        EmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClearAllBtn.IsEnabled = any;

        if (!any)
        {
            EntryPanel.Children.Clear();
            return;
        }
        int idx = keep is null ? 0 : Math.Max(0, groups.FindIndex(g => g.Date == keep));
        DateList.SelectedIndex = idx; // 觸發 RenderSelected
    }

    private void OnSelectionChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e) => RenderSelected();

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

    // ---- 版型元件 ----

    private static StackPanel DateItem(DateGroup g)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = g.Date.ToString("yyyy-MM-dd"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#1B1B1B"),
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"{g.Entries.Count} 筆",
            FontSize = 11,
            Foreground = Brush("#7A7A7A"),
        });
        return sp;
    }

    private UIElement EntryRow(HistoryEntry entry)
    {
        var card = new Border
        {
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#E6E6E6"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左：英文原文（單行截斷）＋時間
        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textCol.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Original) ? "（未偵測到英文文字）" : entry.Original,
            FontSize = 14,
            Foreground = Brush("#1B1B1B"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textCol.Children.Add(new TextBlock
        {
            Text = entry.Timestamp.ToLocalTime().ToString("HH:mm"),
            FontSize = 11.5,
            Foreground = Brush("#8F8F8F"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        Grid.SetColumn(textCol, 0);
        grid.Children.Add(textCol);

        // 右：播音／檢視／刪除
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        actions.Children.Add(ActionButton("▶", "播音（英文原句）", "#2F6FED", "#F0F6FF", "#CFE0FB",
            () => _speech()?.Speak(entry.Original, "en-US", stopPrevious: true)));
        actions.Children.Add(ActionButton("檢視", "開啟中英詳情", "#4A4A4A", "#F5F5F5", "#DCDCDC",
            () => ViewRequested?.Invoke(entry)));
        actions.Children.Add(ActionButton("刪除", "自歷史移除此筆", "#B23B3B", "#FDF2F2", "#F0D2D2",
            () => { _store.Delete(entry.Id); Reload(); }));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private static Button ActionButton(string content, string tip, string fg, string bg, string border, Action onClick)
    {
        var btn = new Button
        {
            Content = content,
            ToolTip = tip,
            Foreground = Brush(fg),
            Background = Brush(bg),
            BorderBrush = Brush(border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 12.5,
            Cursor = Cursors.Hand,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>ESC 關閉歷史視窗（與結果視窗一致的離開慣例）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        base.OnKeyDown(e);
    }
}
