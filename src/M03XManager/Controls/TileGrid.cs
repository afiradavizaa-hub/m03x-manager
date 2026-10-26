using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using M03XManager.ViewModels;

namespace M03XManager.Controls;

/// <summary>
/// 磁贴网格面板 —— 最简实现（选项 A）。
///
/// 刻意不包含：长按识别、摇摆动画、位移动画、RenderTransform、阴影、ZIndex 调整、任何定时器。
/// 之前几轮的问题都来自这些附加物（尤其是"在布局过程中订阅/改状态/启动画"造成的布局回环，
/// 最终演变成栈溢出崩溃）。现在只做两件已被验证的事：
///   1. 占用位图装箱 —— 保证磁贴永不重叠；
///   2. 行高按内容自适应 —— 保证内容不会溢出到邻居上。
///
/// 交互：编辑模式下在磁贴上按下并拖动，指针跨到另一块磁贴时立即交换位置，
///       松开时只写一次配置。没有任何动画，因此不可能抖动或抽搐。
/// </summary>
public sealed class TileGrid : Panel
{
    public const int Columns = 4;
    private const double MinRowHeight = 92;

    private int _dragFrom = -1;
    private bool _dragging;
    private double[] _rowHeights = Array.Empty<double>();
    private DashboardViewModel? _hookedOwner;
    private bool _editMode;

    /// <summary>编辑状态（由视图模型同步而来）。</summary>
    public bool IsEditing => _editMode;

    /// <summary>诊断出口：写到 %APPDATA%\M03XManager\ui-trace.txt，用来确认事件是否真的触发。</summary>
    private static void T(string m)
        => (Application.Current?.MainWindow?.DataContext as MainViewModel)?.Trace(m);

    private double Spacing => TryFindResource("R.TileSpacing") is double d ? d : 12;

    private DashboardViewModel? Owner
        => Children.OfType<FrameworkElement>()
            .Select(c => (c.DataContext as TileViewModel)?.Owner)
            .FirstOrDefault(o => o is not null);

    // ==================== 编辑状态同步（只在非布局时机订阅） ====================

    protected override void OnVisualChildrenChanged(DependencyObject? added, DependencyObject? removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        // 容器刚创建时子项还没拿到 DataContext，必须在 DataContext 就绪后再挂接订阅
        if (removed is FrameworkElement oldFe && oldFe.DataContext is TileViewModel oldTile)
            oldTile.PropertyChanged -= OnTilePropertyChanged;
        if (added is FrameworkElement fe)
        {
            fe.DataContextChanged -= OnChildDataContextChanged;
            fe.DataContextChanged += OnChildDataContextChanged;
            AttachChild(fe);
        }
        HookOwner();
        InvalidateMeasure();
    }

    private void OnChildDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TileViewModel old) old.PropertyChanged -= OnTilePropertyChanged;
        if (sender is FrameworkElement fe) AttachChild(fe);
        HookOwner();
        InvalidateMeasure();
    }

    private void AttachChild(FrameworkElement fe)
    {
        if (fe.DataContext is not TileViewModel tile) return;
        tile.PropertyChanged -= OnTilePropertyChanged;   // 避免重复订阅
        tile.PropertyChanged += OnTilePropertyChanged;
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileViewModel.ColumnSpan) or nameof(TileViewModel.RowSpan)
            or nameof(TileViewModel.ShowTitle))
            InvalidateMeasure();
    }

    private void HookOwner()
    {
        var owner = Owner;
        if (owner is null || ReferenceEquals(owner, _hookedOwner)) return;
        if (_hookedOwner is not null) _hookedOwner.PropertyChanged -= OnOwnerPropertyChanged;
        _hookedOwner = owner;
        owner.PropertyChanged += OnOwnerPropertyChanged;
        SyncEditMode(owner.IsEditing);
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsEditing)) SyncEditMode(_hookedOwner?.IsEditing ?? false);
    }

    private void SyncEditMode(bool editing)
    {
        if (_editMode == editing) return;
        _editMode = editing;
        T($"TileGrid: 编辑态切换为 {editing}");
        if (!editing) { _dragging = false; _dragFrom = -1; ReleaseMouseCaptureOnce(); }
        Cursor = editing ? Cursors.SizeAll : null;
    }

    private void ReleaseMouseCaptureOnce()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    // ==================== 布局 ====================

    protected override Size MeasureOverride(Size availableSize)
    {
        // 注意：布局过程中不订阅、不改状态、不启动动画，避免重新进入布局造成回环
        var spacing = Spacing;
        var width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 1000 : availableSize.Width;
        var cellWidth = Math.Max(80, (width - spacing * (Columns - 1)) / Columns);

        var placements = ComputePlacements();
        var needed = new List<double>();

        foreach (var (child, _, row, colSpan, rowSpan) in placements)
        {
            var w = cellWidth * colSpan + spacing * (colSpan - 1);
            child.Measure(new Size(w, double.PositiveInfinity));
            var desired = (child as FrameworkElement)?.DesiredSize.Height ?? MinRowHeight;
            while (needed.Count < row + rowSpan) needed.Add(MinRowHeight);
            var perRow = Math.Max(MinRowHeight, (desired + spacing * (rowSpan - 1)) / rowSpan);
            for (var r = row; r < row + rowSpan; r++) needed[r] = Math.Max(needed[r], perRow);
        }
        _rowHeights = needed.ToArray();

        foreach (var (child, _, row, colSpan, rowSpan) in placements)
            child.Measure(new Size(cellWidth * colSpan + spacing * (colSpan - 1), RowBlockHeight(row, rowSpan)));

        var total = 0.0;
        for (var i = 0; i < _rowHeights.Length; i++) total += _rowHeights[i] + (i > 0 ? spacing : 0);
        return new Size(width, Math.Max(MinRowHeight, total));
    }

    private double RowBlockHeight(int row, int rowSpan)
    {
        var h = 0.0;
        for (var r = row; r < row + rowSpan && r < _rowHeights.Length; r++) h += _rowHeights[r] + Spacing;
        return Math.Max(MinRowHeight, h - Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var spacing = Spacing;
        var cellWidth = Math.Max(80, (finalSize.Width - spacing * (Columns - 1)) / Columns);

        var rowTops = new double[_rowHeights.Length + 1];
        var y = 0.0;
        for (var i = 0; i < _rowHeights.Length; i++) { rowTops[i] = y; y += _rowHeights[i] + spacing; }
        rowTops[^1] = y;

        foreach (var (child, col, row, colSpan, rowSpan) in ComputePlacements())
        {
            var w = cellWidth * colSpan + spacing * (colSpan - 1);
            var top = row < rowTops.Length ? rowTops[row] : y;
            child.Arrange(new Rect(col * (cellWidth + spacing), top, w, RowBlockHeight(row, rowSpan)));
        }
        return finalSize;
    }

    /// <summary>占用位图 + 首次适配装箱：任何顺序/尺寸组合都不会重叠。</summary>
    private List<(UIElement child, int col, int row, int colSpan, int rowSpan)> ComputePlacements()
    {
        var result = new List<(UIElement, int, int, int, int)>();
        var occupied = new List<bool[]>();

        bool Free(int col, int row, int colSpan, int rowSpan)
        {
            for (var r = row; r < row + rowSpan; r++)
            {
                if (r >= occupied.Count) continue;
                for (var c = col; c < col + colSpan; c++) if (occupied[r][c]) return false;
            }
            return true;
        }

        void Occupy(int col, int row, int colSpan, int rowSpan)
        {
            while (occupied.Count < row + rowSpan) occupied.Add(new bool[Columns]);
            for (var r = row; r < row + rowSpan; r++)
                for (var c = col; c < col + colSpan; c++) occupied[r][c] = true;
        }

        foreach (var child in Children.OfType<UIElement>())
        {
            var tile = (child as FrameworkElement)?.DataContext as TileViewModel;
            var colSpan = Math.Clamp(tile?.ColumnSpan ?? 1, 1, Columns);
            var rowSpan = Math.Clamp(tile?.RowSpan ?? 1, 1, 4);

            var placed = false;
            for (var row = 0; !placed; row++)
                for (var col = 0; col + colSpan <= Columns; col++)
                {
                    if (!Free(col, row, colSpan, rowSpan)) continue;
                    Occupy(col, row, colSpan, rowSpan);
                    result.Add((child, col, row, colSpan, rowSpan));
                    placed = true;
                    break;
                }
        }
        return result;
    }

    // ==================== 交互：编辑态按下即拖，跨磁贴即换位，松手才落盘 ====================

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        T($"TileGrid: 按下 编辑态={_editMode}");
        if (!_editMode) return;
        if (e.OriginalSource is not DependencyObject src) return;
        if (IsInteractive(src)) return;                        // 磁贴内的按钮/输入框不参与拖动
        if (FindChildIndex(src) is not { } index) return;

        _dragFrom = index;
        _dragging = true;
        CaptureMouse();
        T($"TileGrid: 开始拖动 磁贴{index}");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _dragFrom < 0) return;
        var target = IndexAt(e.GetPosition(this));
        if (target < 0 || target == _dragFrom) return;

        // 无任何动画，直接交换位置；集合本身顺序即布局顺序
        MoveInSource(_dragFrom, target);
        _dragFrom = target;
        T($"TileGrid: 交换到 {target}");
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        _dragFrom = -1;
        ReleaseMouseCaptureOnce();
        Owner?.PersistTiles();                                 // 松手只写一次配置
        T("TileGrid: 松手并保存布局");
    }

    /// <summary>退出编辑（由「完成」或空白处点击调用），保证一定落盘。</summary>
    public void ExitEditing()
    {
        _dragging = false;
        _dragFrom = -1;
        ReleaseMouseCaptureOnce();
        _editMode = false;
        Owner?.PersistTiles();
        T("TileGrid: ExitEditing 已保存");
    }

    private int IndexAt(Point p)
    {
        for (var i = 0; i < Children.Count; i++)
            if (Children[i] is FrameworkElement fe &&
                new Rect(fe.TranslatePoint(new Point(0, 0), this), fe.RenderSize).Contains(p))
                return i;
        return -1;
    }

    private static bool IsInteractive(DependencyObject source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is Button or TextBox or PasswordBox or ComboBox or Slider or CheckBox or RadioButton or ScrollBar)
                return true;
            if (node is TileGrid) return false;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private int? FindChildIndex(DependencyObject source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is FrameworkElement fe && Children.Contains(fe)) return Children.IndexOf(fe);
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void MoveInSource(int from, int to)
    {
        if (FindItemsControl()?.ItemsSource is not IList list) return;
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count) return;
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }

    private ItemsControl? FindItemsControl()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is ItemsControl ic) return ic;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
