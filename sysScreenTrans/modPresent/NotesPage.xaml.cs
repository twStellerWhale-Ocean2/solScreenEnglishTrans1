using ScreenTrans.Query;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Border = System.Windows.Controls.Border;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using Orientation = System.Windows.Controls.Orientation;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using Thickness = System.Windows.Thickness;
using CornerRadius = System.Windows.CornerRadius;
using Visibility = System.Windows.Visibility;
using RoutedPropertyChangedEventArgs = System.Windows.RoutedPropertyChangedEventArgs<object>;
using TextTrimming = System.Windows.TextTrimming;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FrameworkElement = System.Windows.FrameworkElement;
using UIElement = System.Windows.UIElement;
using Point = System.Windows.Point;
using DataObject = System.Windows.DataObject;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Adorner = System.Windows.Documents.Adorner;
using AdornerLayer = System.Windows.Documents.AdornerLayer;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using DrawingContext = System.Windows.Media.DrawingContext;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;

namespace ScreenTrans.Present;

/// <summary>
/// 我的筆記分頁（Issue #34／#38 Windows 慣例收斂）：左側**多層資料夾樹如檔案總管**——頂部工具列僅
/// [建立資料夾]（建立即進原地更名）、標準樹節點＋右鍵選單（新增子資料夾／更名 `F2` 原地編輯／刪除 `Del`）、
/// 拖曳移動節點（目標夾高亮、防成環由 <see cref="NotesStore"/> 保證）；右側選取夾之條目拖曳排序
/// （**插入位置指示線**即時回饋）、**右鍵選單**（播音／檢視／底色粉彩盤／刪除，Issue #44）＋**雙擊＝檢視**、
/// 卡片底色套 <see cref="NoteEntry.Color"/>。每次變更即落地 notes.json；語音以 provider 委派取用。
/// </summary>
public partial class NotesPage : UserControl
{
    private const string FmtFolder = "ScreenTrans.NoteFolderId";
    private const string FmtEntry = "ScreenTrans.NoteEntryId";

    private readonly NotesStore _store;
    private readonly Func<ISpeechService?> _speech;
    private NotesData _data;

    public event Action<NoteEntry>? ViewRequested;

    private TreeViewItem? _pressItem;
    private Point _pressPoint;
    private NoteEntry? _entryDrag;
    private Point _entryStart;
    private TreeViewItem? _dropHighlight;   // 目前拖曳滑過之目標夾（高亮回饋，Issue #38）
    private InsertionAdorner? _insertLine;  // 條目拖曳之插入位置指示線（Issue #38）

    public NotesPage(NotesStore store, Func<ISpeechService?> speechProvider)
    {
        InitializeComponent();
        _store = store;
        _speech = speechProvider;
        _data = _store.LoadEnsured();

        NewFolderBtn.Click += (_, _) => CreateFolder(parent: null); // 一律建頂層；子資料夾走節點右鍵選單（檔案總管慣例）
        SortAscBtn.Click += (_, _) => SortEntries(ascending: true);   // 順向 A→Z（Issue #52）
        SortDescBtn.Click += (_, _) => SortEntries(ascending: false); // 反向 Z→A
        ClearEntriesBtn.Click += (_, _) => OnClearEntries();
        FolderTree.SelectedItemChanged += OnFolderSelected;
        FolderTree.PreviewMouseLeftButtonUp += (_, _) => _pressItem = null; // 放開即清，防殘留按壓被後續拖曳劫持
        FolderTree.KeyDown += OnTreeKeyDown;                // F2＝更名、Del＝刪除（檔案總管慣例）
        FolderTree.Drop += OnTreeBackgroundDrop;            // 拖到空白處 → 移到頂層
        FolderTree.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
        FolderTree.DragLeave += (_, _) => SetDropHighlight(null);

        EntryPanel.AllowDrop = true;
        EntryPanel.DragOver += OnEntryAreaDragOver;
        EntryPanel.DragLeave += (_, _) => HideInsertLine();
        EntryPanel.Drop += OnEntryAreaDrop;

        BuildTree();
    }

    public void Reload()
    {
        _data = _store.LoadEnsured();
        BuildTree();
    }

    private NoteFolder? Selected => (FolderTree.SelectedItem as TreeViewItem)?.Tag as NoteFolder;

    // ---- 樹建置 ----

    private void BuildTree()
    {
        NotesStore.SortFolders(_data); // 同層一律依名稱自然排序（檔案總管慣例，Issue #42）
        var keepId = Selected?.Id;
        _dropHighlight = null;
        FolderTree.Items.Clear();
        foreach (var f in _data.Folders)
        {
            FolderTree.Items.Add(MakeItem(f));
        }
        var target = keepId is null ? null : FindItem(FolderTree.Items, keepId);
        (target ?? FirstItem())?.SetValue(TreeViewItem.IsSelectedProperty, true);
        RenderFolder();
    }

    private TreeViewItem MakeItem(NoteFolder f)
    {
        var item = new TreeViewItem
        {
            Header = MakeHeader(f),
            Tag = f,
            IsExpanded = true,
            AllowDrop = true,
        };
        item.PreviewMouseLeftButtonDown += OnItemMouseDown;
        item.PreviewMouseRightButtonDown += (s, _) => ((TreeViewItem)s).IsSelected = true; // 右鍵先選取（檔案總管慣例）
        item.PreviewMouseMove += OnItemMouseMove;
        item.Drop += OnItemDrop;
        item.DragOver += (s, e) =>
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            SetDropHighlight(s as TreeViewItem); // 目標夾高亮（拖曳回饋）
        };
        item.DragLeave += (s, _) =>
        {
            if (ReferenceEquals(_dropHighlight, s))
            {
                SetDropHighlight(null);
            }
        };
        item.ContextMenu = MakeMenu(item, f);
        foreach (var sub in f.Folders)
        {
            item.Items.Add(MakeItem(sub));
        }
        return item;
    }

    private StackPanel MakeHeader(NoteFolder f)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = "", // Segoe MDL2 Assets：FolderHorizontal（檔案總管閉合資料夾）
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brush("#C77D9A"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        });
        sp.Children.Add(new TextBlock { Text = f.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock
        {
            Text = "　" + f.Entries.Count,
            FontSize = 11.5,
            Foreground = Brush("#8A5A6D"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return sp;
    }

    private ContextMenu MakeMenu(TreeViewItem item, NoteFolder f)
    {
        var menu = new ContextMenu();
        var addSub = new MenuItem { Header = "新增子資料夾" };
        addSub.Click += (_, _) => CreateFolder(f);
        var rename = new MenuItem { Header = "更名", InputGestureText = "F2" };
        rename.Click += (_, _) => BeginRename(item);
        var delete = new MenuItem { Header = "刪除", InputGestureText = "Del", Foreground = Brush("#B23B3B") };
        delete.Click += (_, _) => DeleteFolder(f);
        menu.Items.Add(addSub);
        menu.Items.Add(new Separator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        return menu;
    }

    private TreeViewItem? FirstItem() => FolderTree.Items.Count > 0 ? (TreeViewItem)FolderTree.Items[0]! : null;

    private static TreeViewItem? FindItem(System.Windows.Controls.ItemCollection items, string id)
    {
        foreach (TreeViewItem it in items)
        {
            if (it.Tag is NoteFolder f && f.Id == id)
            {
                return it;
            }
            var sub = FindItem(it.Items, id);
            if (sub is not null)
            {
                return sub;
            }
        }
        return null;
    }

    private void OnFolderSelected(object? sender, RoutedPropertyChangedEventArgs e) => RenderFolder();

    private void RenderFolder()
    {
        EntryPanel.Children.Clear();
        var f = Selected;
        bool any = f is not null && f.Entries.Count > 0;
        EmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClearEntriesBtn.IsEnabled = any;
        SortAscBtn.IsEnabled = any;   // 空夾無可排序（Issue #52）
        SortDescBtn.IsEnabled = any;
        if (f is null)
        {
            return;
        }
        foreach (var entry in f.Entries)
        {
            EntryPanel.Children.Add(EntryRow(entry));
        }
    }

    private void Persist()
    {
        NotesStore.SortFolders(_data); // 存檔順序與顯示一致（名稱序）
        _store.Save(_data);
        BuildTree();
    }

    /// <summary>順向/反向排序目前資料夾條目（Issue #52）：依原文自然排序、即時落地 notes.json、重繪。</summary>
    private void SortEntries(bool ascending)
    {
        var f = Selected;
        if (f is null || f.Entries.Count == 0)
        {
            return;
        }
        NotesStore.SortEntries(f, ascending);
        _store.Save(_data);
        RenderFolder();
    }

    /// <summary>右欄[清除全部]：清空選取夾內全部條目（確認對話載明夾名筆數；子夾不動）。</summary>
    private void OnClearEntries()
    {
        var f = Selected;
        if (f is null || f.Entries.Count == 0)
        {
            return;
        }
        if (MessageBox.Show($"清除資料夾「{f.Name}」內全部 {f.Entries.Count} 筆筆記？此動作無法復原（子資料夾不受影響）。",
                "清除全部", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        NotesStore.ClearEntries(_data, f.Id);
        Persist();
    }

    // ---- 資料夾操作（頂部[建立資料夾]＋右鍵選單／F2／Del，原地更名如檔案總管） ----

    private void CreateFolder(NoteFolder? parent)
    {
        var name = NotesStore.NextNewFolderName(_data);
        var created = parent is null
            ? NotesStore.AddFolder(_data, name)
            : NotesStore.AddSubFolder(_data, parent.Id, name);
        if (created is null)
        {
            return;
        }
        Persist(); // 依名稱歸位後入原地更名
        var item = FindItem(FolderTree.Items, created.Id);
        if (item is not null)
        {
            item.IsSelected = true;
            item.BringIntoView();
            BeginRename(item); // 建立即進原地更名（檔案總管慣例）
        }
    }

    /// <summary>原地更名：以 TextBox 就地編輯，Enter／失焦＝確認、Esc＝取消。</summary>
    private void BeginRename(TreeViewItem? item)
    {
        if (item?.Tag is not NoteFolder f)
        {
            return;
        }
        var box = new TextBox { Text = f.Name, MinWidth = 110, FontSize = 13, Padding = new Thickness(2, 0, 2, 0) };
        var done = false;
        void Commit(bool save)
        {
            if (done)
            {
                return;
            }
            done = true;
            if (save && !string.IsNullOrWhiteSpace(box.Text) && box.Text.Trim() != f.Name)
            {
                NotesStore.RenameFolder(_data, f.Id, box.Text);
                Persist(); // 更名後依名稱歸位
                return;
            }
            BuildTree(); // 取消或未變更亦重建，還原一般節點頭
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit(save: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Commit(save: false);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit(save: true);
        item.Header = box;
        item.Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }));
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginRename(FolderTree.SelectedItem as TreeViewItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Selected is { } f)
        {
            DeleteFolder(f);
            e.Handled = true;
        }
    }

    private void DeleteFolder(NoteFolder f)
    {
        var msg = (f.Entries.Count > 0 || f.Folders.Count > 0)
            ? $"刪除資料夾「{f.Name}」及其子夾與 {f.Entries.Count} 筆筆記？此動作無法復原。"
            : $"刪除資料夾「{f.Name}」？";
        if (MessageBox.Show(msg, "刪除資料夾", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        NotesStore.RemoveFolder(_data, f.Id);
        Persist();
    }

    // ---- 資料夾/條目拖曳移動（節點移動如檔案總管；防環由 store；目標夾高亮回饋） ----

    private void SetDropHighlight(TreeViewItem? item)
    {
        if (ReferenceEquals(_dropHighlight, item))
        {
            return;
        }
        if (_dropHighlight is not null)
        {
            _dropHighlight.Background = Brushes.Transparent;
        }
        _dropHighlight = item;
        if (item is not null)
        {
            item.Background = Brush("#F4C2D0");
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressItem = sender as TreeViewItem;
        _pressPoint = e.GetPosition(null);
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        // 僅由被按壓的節點本身啟動拖曳：防止殘留 _pressItem 在滑鼠（因其他拖曳）持鍵掃過樹時誤啟動資料夾拖曳
        if (_pressItem is null || !ReferenceEquals(sender, _pressItem) || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _pressPoint.X) < 6 && Math.Abs(p.Y - _pressPoint.Y) < 6)
        {
            return;
        }
        if (_pressItem.Tag is NoteFolder f)
        {
            var moving = _pressItem;
            _pressItem = null;
            DragDrop.DoDragDrop(moving, new DataObject(FmtFolder, f.Id), DragDropEffects.Move);
            SetDropHighlight(null); // 拖曳結束（含取消）清除高亮
        }
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        if ((sender as TreeViewItem)?.Tag is not NoteFolder target)
        {
            return;
        }
        if (e.Data.GetDataPresent(FmtFolder) && e.Data.GetData(FmtFolder) is string fid)
        {
            NotesStore.MoveFolder(_data, fid, target.Id); // 防移入自身/子孫由 store 保證
            Persist();
        }
        else if (e.Data.GetDataPresent(FmtEntry) && e.Data.GetData(FmtEntry) is string eid)
        {
            NotesStore.MoveEntry(_data, eid, target.Id);
            Persist();
        }
        e.Handled = true;
    }

    private void OnTreeBackgroundDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        if (e.Handled)
        {
            return; // 已由某節點處理
        }
        if (e.Data.GetDataPresent(FmtFolder) && e.Data.GetData(FmtFolder) is string fid)
        {
            NotesStore.MoveFolder(_data, fid, null); // 移到頂層
            Persist();
        }
    }

    // ---- 條目版型與排序（拖曳中顯示插入位置指示線，Issue #38） ----

    // 條目卡（Issue #44）：底色套 NoteEntry.Color；操作循 Windows 清單慣例——右鍵選單＋雙擊檢視、無常駐按鈕列。
    private UIElement EntryRow(NoteEntry entry)
    {
        var card = new Border
        {
            Background = SafeBrush(entry.Color, "#FFFFFF"),
            BorderBrush = Brush("#F4C2D0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
            AllowDrop = true, // 事件交由 EntryPanel 統一處理（冒泡），卡片僅作為有效放置目標
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 行尾播音鈕（Issue #56）

        var handle = new TextBlock
        {
            Text = "≡",
            FontSize = 16,
            Foreground = Brush("#C77D9A"),
            Cursor = Cursors.SizeAll, // 四向移動：可上下排序亦可拖入左樹資料夾（Issue #46）
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0),
            ToolTip = "拖曳排序／拖到左側資料夾移動",
        };
        handle.PreviewMouseLeftButtonDown += (_, ev) => { _entryDrag = entry; _entryStart = ev.GetPosition(null); };
        handle.PreviewMouseLeftButtonUp += (_, _) => _entryDrag = null; // 放開即清（對稱防殘留）
        handle.PreviewMouseMove += OnEntryHandleMove;
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Original) ? "（無內容）" : entry.Original,
            FontSize = 14,
            Foreground = Brush("#3A2C33"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // 行尾播音鈕（Issue #56）：最高頻動作一鍵可達，其餘（檢視/底色/刪除）仍走右鍵選單。
        // Button 自行處理點擊、不冒泡至卡片（故單擊播音不觸發雙擊檢視、不啟動拖曳）。
        var playBtn = new Button
        {
            Content = "", // Segoe MDL2 Assets：Play
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brush("#2F6FED"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
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
            if (e.ClickCount == 2) // 雙擊＝檢視（Windows 清單慣例）
            {
                ViewRequested?.Invoke(entry);
                e.Handled = true;
            }
        };
        return card;
    }

    private ContextMenu MakeEntryMenu(NoteEntry entry)
    {
        var menu = new ContextMenu();
        var play = new MenuItem { Header = "▶ 播音", Foreground = Brush("#2F6FED") };
        play.Click += (_, _) => _speech()?.Speak(entry.Original, "en-US", stopPrevious: true);
        var view = new MenuItem { Header = "檢視" };
        view.Click += (_, _) => ViewRequested?.Invoke(entry);

        var color = new MenuItem { Header = "底色" };
        color.Items.Add(ColorItem(entry, "無底色", ""));
        foreach (var (name, hex) in NoteColors.Palette) // 集中色盤（Issue #55）
        {
            color.Items.Add(ColorItem(entry, name, hex));
        }

        var delete = new MenuItem { Header = "刪除", Foreground = Brush("#B23B3B") };
        delete.Click += (_, _) => { NotesStore.RemoveEntry(_data, entry.Id); Persist(); };

        menu.Items.Add(play);
        menu.Items.Add(view);
        menu.Items.Add(color);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    private MenuItem ColorItem(NoteEntry entry, string name, string hex)
    {
        var item = new MenuItem
        {
            Header = name,
            IsCheckable = false,
            Icon = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                Background = SafeBrush(hex, "#FFFFFF"),
                BorderBrush = Brush("#D8B4C2"),
                BorderThickness = new Thickness(1),
            },
        };
        if ((entry.Color ?? "") == hex)
        {
            item.FontWeight = System.Windows.FontWeights.SemiBold;
            item.Header = name + "　✓"; // 目前色標記
        }
        item.Click += (_, _) =>
        {
            NotesStore.SetEntryColor(_data, entry.Id, hex);
            Persist();
        };
        return item;
    }

    /// <summary>hex 轉筆刷；空或無效值回退預設色（舊檔容錯）。</summary>
    private static SolidColorBrush SafeBrush(string? hex, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { return Brush(hex!); }
            catch { /* 無效色值回退 */ }
        }
        return Brush(fallback);
    }

    private void OnEntryHandleMove(object sender, MouseEventArgs e)
    {
        if (_entryDrag is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _entryStart.X) < 6 && Math.Abs(p.Y - _entryStart.Y) < 6)
        {
            return;
        }
        var moving = _entryDrag;
        _entryDrag = null;
        DragDrop.DoDragDrop(EntryPanel, new DataObject(FmtEntry, moving.Id), DragDropEffects.Move);
        HideInsertLine(); // 拖曳結束（含取消／落在樹側）清除指示線
    }

    /// <summary>條目拖曳滑過右側清單 → 於預定落點顯示插入位置指示線（標準拖放回饋）。</summary>
    private void OnEntryAreaDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(FmtEntry))
        {
            e.Effects = DragDropEffects.None; // 資料夾拖入右側無意義 → 標準「不可放置」回饋
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ShowInsertLine(SlotIndex(e.GetPosition(EntryPanel).Y));
    }

    // 條目落下 → 同夾排序（插入槽位所見即所得；來自他夾之條目交由左側資料夾 drop 處理移動）
    private void OnEntryAreaDrop(object sender, DragEventArgs e)
    {
        HideInsertLine();
        var f = Selected;
        if (f is null || !e.Data.GetDataPresent(FmtEntry) || e.Data.GetData(FmtEntry) is not string eid)
        {
            return;
        }
        int from = f.Entries.FindIndex(x => x.Id == eid);
        if (from < 0)
        {
            return;
        }
        int slot = SlotIndex(e.GetPosition(EntryPanel).Y);
        int to = slot > from ? slot - 1 : slot; // 插入槽位 → 最終索引（移除自身後前移一位）
        NotesStore.Reorder(f, from, to);
        _store.Save(_data);
        RenderFolder();
        e.Handled = true;
    }

    /// <summary>以 Y 座標求插入槽位（0..Count）：每列以中線分上下半。</summary>
    private int SlotIndex(double y)
    {
        for (int i = 0; i < EntryPanel.Children.Count; i++)
        {
            var c = (FrameworkElement)EntryPanel.Children[i];
            double top = c.TranslatePoint(new Point(0, 0), EntryPanel).Y;
            if (y < top + c.ActualHeight / 2)
            {
                return i;
            }
        }
        return EntryPanel.Children.Count;
    }

    private void ShowInsertLine(int slot)
    {
        if (_insertLine is null)
        {
            var layer = AdornerLayer.GetAdornerLayer(EntryPanel);
            if (layer is null)
            {
                return;
            }
            _insertLine = new InsertionAdorner(EntryPanel);
            layer.Add(_insertLine);
        }
        double y = 2;
        int n = EntryPanel.Children.Count;
        if (n > 0)
        {
            if (slot < n)
            {
                var c = (FrameworkElement)EntryPanel.Children[slot];
                y = c.TranslatePoint(new Point(0, 0), EntryPanel).Y - 4; // 落點列上緣（含列距中線）
            }
            else
            {
                var last = (FrameworkElement)EntryPanel.Children[n - 1];
                y = last.TranslatePoint(new Point(0, 0), EntryPanel).Y + last.ActualHeight + 4;
            }
        }
        _insertLine.SetY(y);
    }

    private void HideInsertLine()
    {
        if (_insertLine is not null)
        {
            AdornerLayer.GetAdornerLayer(EntryPanel)?.Remove(_insertLine);
            _insertLine = null;
        }
    }

    /// <summary>插入位置指示線（WPF 標準拖放回饋模式：Adorner 疊加水平線＋左端圓點）。</summary>
    private sealed class InsertionAdorner : Adorner
    {
        private static readonly SolidColorBrush LineBrush = new(Color.FromRgb(0x2F, 0x6F, 0xED));
        private static readonly Pen LinePen = new(LineBrush, 2.5);
        private double _y;

        static InsertionAdorner()
        {
            LineBrush.Freeze();
            LinePen.Freeze();
        }

        public InsertionAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

        public void SetY(double y)
        {
            if (Math.Abs(_y - y) > 0.5)
            {
                _y = y;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var width = ((FrameworkElement)AdornedElement).ActualWidth;
            dc.DrawEllipse(LineBrush, null, new Point(3, _y), 3.5, 3.5);
            dc.DrawLine(LinePen, new Point(6, _y), new Point(width, _y));
        }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
