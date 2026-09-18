using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopBoxesUI.Controls;

public sealed class VirtualizingIconPanel :
    VirtualizingPanel,
    IScrollInfo
{
    private int _firstIndex = -1;
    private int _lastIndex = -1;

    private int _columns = 1;

    private int _itemCount;
    private int _rowCount;

    private double _offsetY;

    private Size _viewport;
    private Size _extent;

    private ItemsControl? _owner;
    private IItemContainerGenerator? _generator;

    private bool _isRebuilding;

    #region Dependency Properties

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingIconPanel),
            new FrameworkPropertyMetadata(
                96.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingIconPanel),
            new FrameworkPropertyMetadata(
                96.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(VirtualizingIconPanel),
            new FrameworkPropertyMetadata(
                8.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(VirtualizingIconPanel),
            new FrameworkPropertyMetadata(
                8.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CacheRowsProperty =
        DependencyProperty.Register(
            nameof(CacheRows),
            typeof(int),
            typeof(VirtualizingIconPanel),
            new FrameworkPropertyMetadata(
                2,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    #endregion

    #region Properties

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public int CacheRows
    {
        get => (int)GetValue(CacheRowsProperty);
        set => SetValue(CacheRowsProperty, value);
    }

    #endregion

    #region IScrollInfo

    public bool CanHorizontallyScroll
    {
        get => false;
        set { }
    }

    public bool CanVerticallyScroll
    {
        get => true;
        set { }
    }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _offsetY;

    public ScrollViewer? ScrollOwner { get; set; }

    public void SetHorizontalOffset(double offset)
    {
        // Horizontal scrolling intentionally disabled.
    }

    public void SetVerticalOffset(double offset)
    {
        double max =
            Math.Max(
                0,
                _extent.Height - _viewport.Height);

        offset = Math.Clamp(
            offset,
            0,
            max);

        if (Math.Abs(offset - _offsetY) < 0.1)
            return;

        _offsetY = offset;

        InvalidateMeasure();

        ScrollOwner?.InvalidateScrollInfo();
    }

    public void LineUp()
    {
        SetVerticalOffset(
            _offsetY - RowHeight);
    }

    public void LineDown()
    {
        SetVerticalOffset(
            _offsetY + RowHeight);
    }

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageUp()
    {
        SetVerticalOffset(
            _offsetY - _viewport.Height);
    }

    public void PageDown()
    {
        SetVerticalOffset(
            _offsetY + _viewport.Height);
    }

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelUp()
    {
        SetVerticalOffset(
            _offsetY - RowHeight * 3);
    }

    public void MouseWheelDown()
    {
        SetVerticalOffset(
            _offsetY + RowHeight * 3);
    }

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public Rect MakeVisible(
        Visual visual,
        Rect rectangle)
    {
        if (visual is UIElement element)
        {
            int index = GetIndex(element);

            if (index >= 0)
            {
                BringIndexIntoView(index);

                return GetItemRect(index);
            }
        }

        return rectangle;
    }

    #endregion

    #region Layout

    private double RowHeight =>
        ItemHeight + VerticalSpacing;

    private double ColumnWidth =>
        ItemWidth + HorizontalSpacing;

    protected override Size MeasureOverride(
    Size availableSize)
    {
        EnsureOwner();

        _itemCount =
            _owner?.Items.Count ?? 0;

        if (_itemCount == 0)
        {
            CleanupAll();

            _columns = 1;
            _rowCount = 0;

            double w0 = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            double h0 = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            _extent = new Size(w0, 0);
            _viewport = new Size(w0, h0);

            ScrollOwner?.InvalidateScrollInfo();

            return new Size(w0, h0);
        }

        int oldColumns = _columns;

        CalculateLayout(
            availableSize);

        if (oldColumns != _columns &&
            _firstIndex >= 0)
        {
            CleanupAll();
        }

        ClampOffset();

        ScrollOwner?.InvalidateScrollInfo();

        int firstVisibleRow =
            (int)(_offsetY / RowHeight);

        int visibleRows =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    _viewport.Height / RowHeight));

        int firstRow =
            Math.Max(
                0,
                firstVisibleRow - CacheRows);

        int lastRow =
            Math.Min(
                _rowCount - 1,
                firstVisibleRow +
                visibleRows +
                CacheRows);

        int firstIndex =
            firstRow * _columns;

        int lastIndex =
            Math.Min(
                _itemCount - 1,
                ((lastRow + 1) * _columns) - 1);

        Realize(
            firstIndex,
            lastIndex);

        Cleanup(
            firstIndex,
            lastIndex);

        double fw = double.IsInfinity(availableSize.Width) ? _viewport.Width : availableSize.Width;
        double fh = double.IsInfinity(availableSize.Height) ? _viewport.Height : availableSize.Height;
        if (double.IsInfinity(fw) || double.IsNaN(fw)) fw = _viewport.Width;
        if (double.IsInfinity(fh) || double.IsNaN(fh)) fh = _viewport.Height;
        if (double.IsInfinity(fw)) fw = 0;
        if (double.IsInfinity(fh)) fh = 0;
        return new Size(fw, fh);
    }

    protected override Size ArrangeOverride(
        Size finalSize)
    {
        for (int i = 0;
             i < InternalChildren.Count;
             i++)
        {
            UIElement child =
                InternalChildren[i];

            int index =
                GetIndex(child);

            if (index < 0)
                continue;

            int row =
                index / _columns;

            int column =
                index % _columns;

            // This panel owns scrolling (IScrollInfo, no ScrollContentPresenter transform):
            // children must be arranged in viewport space, i.e. minus the scroll offset.
            // Arranging at absolute row positions is what made icons "disappear" on scroll.
            child.Arrange(
                new Rect(
                    column * ColumnWidth,
                    (row * RowHeight) - _offsetY,
                    ItemWidth,
                    ItemHeight));
        }

        return finalSize;
    }

    private void CalculateLayout(
        Size availableSize)
    {
        double width = Math.Max(1, ItemWidth);
        double spacing = Math.Max(0, HorizontalSpacing);
        double availW = availableSize.Width;
        double availH = availableSize.Height;
        if (double.IsInfinity(availW) || double.IsNaN(availW) || availW <= 0)
            availW = double.IsInfinity(_viewport.Width) || _viewport.Width <= 0 ? 400 : _viewport.Width;
        if (double.IsInfinity(availH) || double.IsNaN(availH) || availH <= 0)
            availH = double.IsInfinity(_viewport.Height) || _viewport.Height <= 0 ? 400 : _viewport.Height;

        _columns = Math.Max(1, (int)Math.Floor((availW + spacing) / (width + spacing)));

        _rowCount = (_itemCount + _columns - 1) / _columns;

        double extentHeight = _rowCount == 0 ? 0 : (_rowCount * RowHeight) - VerticalSpacing;

        _extent = new Size(availW, Math.Max(0, extentHeight));
        _viewport = new Size(availW, availH);
    }

    private void ClampOffset()
    {
        double max =
            Math.Max(
                0,
                _extent.Height -
                _viewport.Height);

        if (_offsetY > max)
            _offsetY = max;

        if (_offsetY < 0)
            _offsetY = 0;
    }

    #endregion

    #region Virtualization

    private void Realize(
        int firstIndex,
        int lastIndex)
    {
        if (firstIndex > lastIndex)
        {
            CleanupAll();
            return;
        }

        if (_generator == null)
            return;

        /*
         * If the number of columns changed, the old
         * realized index mapping is no longer trustworthy.
         */
        if (_firstIndex < 0)
        {
            Rebuild(
                firstIndex,
                lastIndex);

            return;
        }

        /*
         * No overlap with the realized window (far jump from scrollbar drag,
         * BringIndexIntoView, or a collection reset): generating the whole gap
         * just to trim it is O(distance) waste — rebuild the window directly.
         */
        if (firstIndex > _lastIndex ||
            lastIndex < _firstIndex)
        {
            Rebuild(
                firstIndex,
                lastIndex);

            return;
        }

        /*
         * Overlapping window: extend incrementally and let Cleanup() trim the
         * far side. (An unconditional Rebuild here — as before — regenerates every
         * visible container on each scroll tick and defeats virtualization.)
         */

        if (firstIndex < _firstIndex)
        {
            GenerateRange(
                firstIndex,
                _firstIndex - 1);
        }

        if (lastIndex > _lastIndex)
        {
            GenerateRange(
                _lastIndex + 1,
                lastIndex);
        }

        _firstIndex =
            Math.Min(
                _firstIndex,
                firstIndex);

        _lastIndex =
            Math.Max(
                _lastIndex,
                lastIndex);
    }

    //private bool _columnsChangedSinceLastLayout;

    private void Rebuild(
        int firstIndex,
        int lastIndex)
    {
        if (_isRebuilding)
            return;

        _isRebuilding = true;

        try
        {
            CleanupAll();

            GenerateRange(
                firstIndex,
                lastIndex);

            _firstIndex = firstIndex;
            _lastIndex = lastIndex;
        }
        finally
        {
            _isRebuilding = false;
        }
    }

    private void GenerateRange(
        int firstIndex,
        int lastIndex)
    {
        if (_generator == null)
            return;

        if (firstIndex > lastIndex)
            return;

        GeneratorPosition position =
            _generator.GeneratorPositionFromIndex(
                firstIndex);

        int childIndex =
            position.Offset == 0
                ? position.Index
                : position.Index + 1;

        using IDisposable generation =
            _generator.StartAt(
                position,
                GeneratorDirection.Forward,
                true);

        for (
            int index = firstIndex;
            index <= lastIndex;
            index++)
        {
            bool newlyRealized;

            UIElement? child =
                _generator.GenerateNext(
                    out newlyRealized)
                as UIElement;

            if (child == null)
                continue;

            if (newlyRealized)
            {
                InsertInternalChild(
                    Math.Min(
                        childIndex,
                        InternalChildren.Count),
                    child);

                _generator.PrepareItemContainer(
                    child);
            }

            child.Measure(
                new Size(
                    ItemWidth,
                    ItemHeight));

            childIndex++;
        }
    }

    private void Cleanup(
        int firstIndex,
        int lastIndex)
    {
        if (_firstIndex < 0 ||
            InternalChildren.Count == 0)
            return;

        /*
         * Because all containers are contiguous,
         * removing from either end is cheap.
         */

        if (_firstIndex < firstIndex)
        {
            int count =
                firstIndex -
                _firstIndex;

            RemoveRealized(
                0,
                count);

            _firstIndex =
                firstIndex;
        }

        if (_lastIndex > lastIndex)
        {
            int count =
                _lastIndex -
                lastIndex;

            int childIndex =
                InternalChildren.Count -
                count;

            RemoveRealized(
                childIndex,
                count);

            _lastIndex =
                lastIndex;
        }

        if (InternalChildren.Count == 0)
        {
            _firstIndex = -1;
            _lastIndex = -1;
        }
    }

    private void RemoveRealized(
        int childIndex,
        int count)
    {
        if (count <= 0)
            return;

        if (_generator == null)
            return;

        childIndex =
            Math.Clamp(
                childIndex,
                0,
                InternalChildren.Count);

        count =
            Math.Min(
                count,
                InternalChildren.Count -
                childIndex);

        if (count <= 0)
            return;

        _generator.Remove(
            new GeneratorPosition(
                childIndex,
                0),
            count);

        RemoveInternalChildRange(
            childIndex,
            count);
    }

    private void CleanupAll()
    {
        if (InternalChildren.Count == 0)
        {
            _firstIndex = -1;
            _lastIndex = -1;
            return;
        }

        if (_generator != null)
        {
            _generator.Remove(
                new GeneratorPosition(0, 0),
                InternalChildren.Count);
        }

        RemoveInternalChildRange(
            0,
            InternalChildren.Count);

        _firstIndex = -1;
        _lastIndex = -1;
    }

    #endregion

    #region Items Changed

    protected override void OnItemsChanged(
        object sender,
        ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(
            sender,
            args);

        /*
         * Collection modifications change the
         * index → container mapping.
         *
         * Rebuild only the virtualized region.
         */
        CleanupAll();

        InvalidateMeasure();
    }

    #endregion

    #region Bring Into View

    public new void BringIndexIntoView(
        int index)
    {
        if (index < 0 ||
            index >= _itemCount)
            return;

        int row =
            index / _columns;

        double top =
            row * RowHeight;

        double bottom =
            top + ItemHeight;

        if (top < _offsetY)
        {
            SetVerticalOffset(top);
        }
        else if (
            bottom >
            _offsetY +
            _viewport.Height)
        {
            SetVerticalOffset(
                bottom -
                _viewport.Height);
        }
    }

    private Rect GetItemRect(
        int index)
    {
        int row =
            index / _columns;

        int column =
            index % _columns;

        return new Rect(
            column * ColumnWidth,
            row * RowHeight,
            ItemWidth,
            ItemHeight);
    }

    private int GetIndex(
        UIElement element)
    {
        int childIndex =
            InternalChildren.IndexOf(element);

        if (childIndex < 0 ||
            _firstIndex < 0)
            return -1;

        return _firstIndex +
               childIndex;
    }

    #endregion

    #region Helpers

    private void EnsureOwner()
    {
        _owner ??=
            ItemsControl.GetItemsOwner(this);

        _generator ??=
            ItemContainerGenerator;
    }

    #endregion

    #region Mouse Wheel

    protected override void OnMouseWheel(
        MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
            MouseWheelUp();
        else
            MouseWheelDown();

        e.Handled = true;
    }

    #endregion

    protected override bool HasLogicalOrientation =>
        true;

    protected override Orientation LogicalOrientation =>
        Orientation.Vertical;
}