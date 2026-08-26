using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Win32;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace STranslate.Views;

/// <summary>
/// 可多实例并存的原位图片翻译窗口，物理像素坐标为权威布局。
/// </summary>
public partial class PinnedImageTranslateWindow
{
    private const int WmNcHitTest = 0x0084;
    private const int WmDpiChanged = 0x02E0;
    private const int HtTransparent = -1;
    private const double ToolbarHeightFallbackDip = 44;
    private const double ToolbarWidthFallbackDip = 760;
    private const double ToolbarGapHDip = 8;
    private const double ToolbarGapVDip = 6;

    private readonly PinnedWindowController _controller;
    private readonly IServiceScope _serviceScope;
    private readonly PinnedImageTranslateViewModel _viewModel;
    private readonly PinnedImageTranslateChromeWindow _chromeWindow;
    private HwndSource? _hwndSource;
    private PinnedImageTranslateSource? _source;
    private DrawingRectangle _imageScreenRectPhysical;
    private DrawingRectangle _outerWindowRectPhysical;
    private DrawingRectangle _toolbarScreenRectPhysical;
    private DrawingPoint _gestureStartCursorPhysical;
    private DrawingRectangle _gestureStartImageRectPhysical;
    private GestureOwner _gestureOwner;
    private bool _potentialDrag;
    private bool _isDragging;
    private double _dragThresholdXPhysical;
    private double _dragThresholdYPhysical;
    private ContextMenu? _activeContextMenu;
    private bool? _toolbarTooltipsEnabledBeforeCapture;
    private bool _sourceInitialized;
    private bool _isClosing;

    public PinnedImageTranslateWindow(PinnedWindowController controller)
    {
        _controller = controller;
        _serviceScope = Ioc.Default.CreateScope();
        _viewModel = _serviceScope.ServiceProvider.GetRequiredService<PinnedImageTranslateViewModel>();
        DataContext = _viewModel;

        InitializeComponent();
        _chromeWindow = new PinnedImageTranslateChromeWindow();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    internal void Initialize(PinnedImageTranslateSource source)
    {
        _source = source;
        _imageScreenRectPhysical = source.PhysicalBounds;
        _viewModel.Initialize(source);
        RecalculateLayout();
    }

    internal void CloseTransientUiForCapture()
    {
        if (_activeContextMenu is { IsOpen: true })
            _activeContextMenu.IsOpen = false;

        foreach (var comboBox in GetToolbarComboBoxes())
            comboBox.IsDropDownOpen = false;

        if (_toolbarTooltipsEnabledBeforeCapture == null)
        {
            _toolbarTooltipsEnabledBeforeCapture = ToolTipService.GetIsEnabled(PART_ToolbarBorder);
            ToolTipService.SetIsEnabled(PART_ToolbarBorder, false);
        }
    }

    internal void RestoreTransientUiAfterCapture()
    {
        if (_toolbarTooltipsEnabledBeforeCapture is not { } wasEnabled)
            return;

        ToolTipService.SetIsEnabled(PART_ToolbarBorder, wasEnabled);
        _toolbarTooltipsEnabledBeforeCapture = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _sourceInitialized = true;
        Win32Helper.HideFromAltTab(this);
        _hwndSource = Win32Helper.AddWndProcHook(this, WndProc);

        // HWND 建立后按实际 DPI 重排。
        RecalculateLayout();
        _controller.OnWindowSourceInitialized(this);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_isClosing)
            return;
        UpdateChromeVisualAndBounds();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_isClosing)
            return;
        UpdateChromeVisualAndBounds();
    }

    internal bool SetCaptureCloaked(bool cloaked)
    {
        var contentResult = Win32Helper.SetWindowCloaked(this, cloaked);
        var chromeResult = _chromeWindow.SetCloaked(cloaked);
        return contentResult && chromeResult;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.ChangedButton != MouseButton.Left || _isClosing)
            return;

        _gestureOwner = ResolveGestureOwner(e);
        if (_gestureOwner is GestureOwner.Toolbar or GestureOwner.Text or GestureOwner.None)
            return;

        if (_gestureOwner != GestureOwner.ImageContentBlank || !TryGetCursorPosition(out var cursor))
            return;

        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            Close();
            return;
        }

        PART_ImageZoom.ClearTextSelection();
        Focus();

        _gestureStartCursorPhysical = cursor;
        _gestureStartImageRectPhysical = _imageScreenRectPhysical;
        var dpi = GetAuthoritativeDpi();
        _dragThresholdXPhysical = Math.Max(1, SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX);
        _dragThresholdYPhysical = Math.Max(1, SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY);
        _potentialDrag = true;
        _isDragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_gestureOwner != GestureOwner.ImageContentBlank ||
            !_potentialDrag ||
            e.LeftButton != MouseButtonState.Pressed ||
            !TryGetCursorPosition(out var cursor))
            return;

        var dx = cursor.X - _gestureStartCursorPhysical.X;
        var dy = cursor.Y - _gestureStartCursorPhysical.Y;

        if (!_isDragging &&
            Math.Abs(dx) < _dragThresholdXPhysical &&
            Math.Abs(dy) < _dragThresholdYPhysical)
            return;

        _isDragging = true;
        var desiredImageLeft = _gestureStartImageRectPhysical.Left + dx;
        var desiredImageTop = _gestureStartImageRectPhysical.Top + dy;
        MoveByPhysicalDelta(
            desiredImageLeft - _imageScreenRectPhysical.Left,
            desiredImageTop - _imageScreenRectPhysical.Top,
            syncLogicalBounds: false);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (e.ChangedButton != MouseButton.Left || _gestureOwner != GestureOwner.ImageContentBlank)
            return;

        var wasDragging = _isDragging;
        ResetPointerGesture();
        if (wasDragging)
            SyncLogicalBoundsAndCorrectPhysical();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gestureOwner == GestureOwner.ImageContentBlank)
            ResetPointerGesture(releaseCapture: false);
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);
        if (_isClosing || IsEventInsideToolbar(e.OriginalSource as DependencyObject))
            return;

        if (!TryGetCursorPosition(out var cursor) || !_imageScreenRectPhysical.Contains(cursor))
            return;

        var pointOnImageZoom = e.GetPosition(PART_ImageZoom);
        if (PART_ImageZoom.IsPointOverSelectableText(pointOnImageZoom))
            ShowTextContextMenu();
        else
            ShowWindowContextMenu();

        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_isClosing || IsKeyboardFocusInsideToolbarOrMenu())
            return;

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };

        if (dx == 0 && dy == 0)
            return;

        MoveByPhysicalDelta(dx, dy, syncLogicalBounds: true);
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        CloseTransientUiForCapture();
        _viewModel.CancelCurrentOperation();
        // Closing 后仍可能收到 Deactivated，因此先隐藏 Chrome，延后到 OnClosed 再关闭。
        _chromeWindow.HideForOwnerClosing();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _controller.Unregister(this);
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (_hwndSource != null)
                _hwndSource.RemoveHook(WndProc);
        }
        finally
        {
            _chromeWindow.CloseSafely();
            ModernWindowLifecycle.Release(this, _serviceScope.Dispose);
            base.OnClosed(e);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PinnedImageTranslateViewModel.IsToolbarVisible))
            Dispatcher.BeginInvoke(RecalculateLayout, DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(PinnedImageTranslateViewModel.IsShadowEnabled))
            Dispatcher.BeginInvoke(UpdateChromeVisualAndBounds, DispatcherPriority.Loaded);
    }

    private GestureOwner ResolveGestureOwner(MouseButtonEventArgs e)
    {
        if (IsEventInsideToolbar(e.OriginalSource as DependencyObject))
            return GestureOwner.Toolbar;

        if (!TryGetCursorPosition(out var cursor) || !_imageScreenRectPhysical.Contains(cursor))
            return GestureOwner.None;

        return PART_ImageZoom.IsPointOverSelectableText(e.GetPosition(PART_ImageZoom))
            ? GestureOwner.Text
            : GestureOwner.ImageContentBlank;
    }

    private void RecalculateLayout()
    {
        if (_isClosing || _source == null || _imageScreenRectPhysical.Width <= 0 || _imageScreenRectPhysical.Height <= 0)
            return;

        var dpi = GetAuthoritativeDpi();
        var workArea = GetPhysicalWorkArea(
            _imageScreenRectPhysical.Left + _imageScreenRectPhysical.Width / 2,
            _imageScreenRectPhysical.Top + _imageScreenRectPhysical.Height / 2);

        _toolbarScreenRectPhysical = DrawingRectangle.Empty;
        var outerBounds = _imageScreenRectPhysical;

        if (_viewModel.IsToolbarVisible)
        {
            var toolbarSizeDip = MeasureToolbarSizeDip();
            var compactLayout = ImageTranslateCompactWindowPlacement.CreateLayout(
                imageBounds: _imageScreenRectPhysical,
                workArea: workArea,
                dpiScaleX: dpi.DpiScaleX,
                dpiScaleY: dpi.DpiScaleY,
                minWidthDip: 1,
                minImageHeightDip: 1,
                toolbarWidthDip: toolbarSizeDip.Width,
                toolbarHeightDip: toolbarSizeDip.Height,
                gapHDip: ToolbarGapHDip,
                gapVDip: ToolbarGapVDip,
                windowMarginDip: 1);

            _toolbarScreenRectPhysical = new DrawingRectangle(
                compactLayout.WindowBounds.Left + compactLayout.ToolbarX,
                compactLayout.WindowBounds.Top + compactLayout.ToolbarY,
                compactLayout.ToolbarWidth,
                compactLayout.ToolbarHeight);
            outerBounds = DrawingRectangle.Union(_imageScreenRectPhysical, compactLayout.WindowBounds);
        }

        _outerWindowRectPhysical = outerBounds;
        ApplyPhysicalModelToVisualTree(dpi, moveHwnd: _sourceInitialized);
    }

    private Size MeasureToolbarSizeDip()
    {
        PART_ToolbarPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var panelSize = PART_ToolbarPanel.DesiredSize;
        var width = panelSize.Width + PART_ToolbarBorder.Padding.Left + PART_ToolbarBorder.Padding.Right;
        var height = panelSize.Height + PART_ToolbarBorder.Padding.Top + PART_ToolbarBorder.Padding.Bottom;

        if (width <= 1)
            width = ToolbarWidthFallbackDip;
        if (height <= 1)
            height = ToolbarHeightFallbackDip;

        return new Size(width, height);
    }

    private void ApplyPhysicalModelToVisualTree(DpiScale dpi, bool moveHwnd)
    {
        var sx = Math.Max(0.01, dpi.DpiScaleX);
        var sy = Math.Max(0.01, dpi.DpiScaleY);
        var dipBounds = ImageTranslateCompactWindowPlacement.ToDipBounds(_outerWindowRectPhysical, sx, sy);

        Left = dipBounds.Left;
        Top = dipBounds.Top;
        Width = dipBounds.Width;
        Height = dipBounds.Height;

        PART_ImageSurface.HorizontalAlignment = HorizontalAlignment.Left;
        PART_ImageSurface.VerticalAlignment = VerticalAlignment.Top;
        PART_ImageSurface.Width = _imageScreenRectPhysical.Width / sx;
        PART_ImageSurface.Height = _imageScreenRectPhysical.Height / sy;
        PART_ImageSurface.Margin = new Thickness(
            (_imageScreenRectPhysical.Left - _outerWindowRectPhysical.Left) / sx,
            (_imageScreenRectPhysical.Top - _outerWindowRectPhysical.Top) / sy,
            0,
            0);

        if (_viewModel.IsToolbarVisible && !_toolbarScreenRectPhysical.IsEmpty)
        {
            PART_ToolbarBorder.HorizontalAlignment = HorizontalAlignment.Left;
            PART_ToolbarBorder.VerticalAlignment = VerticalAlignment.Top;
            PART_ToolbarBorder.Width = _toolbarScreenRectPhysical.Width / sx;
            PART_ToolbarBorder.Height = _toolbarScreenRectPhysical.Height / sy;
            PART_ToolbarBorder.Margin = new Thickness(
                (_toolbarScreenRectPhysical.Left - _outerWindowRectPhysical.Left) / sx,
                (_toolbarScreenRectPhysical.Top - _outerWindowRectPhysical.Top) / sy,
                0,
                0);
        }

        if (moveHwnd)
        {
            Win32Helper.SetWindowPhysicalBounds(
                this,
                _outerWindowRectPhysical.Left,
                _outerWindowRectPhysical.Top,
                _outerWindowRectPhysical.Width,
                _outerWindowRectPhysical.Height);
        }

        if (_sourceInitialized)
            UpdateChromeVisualAndBounds();
    }

    private void MoveByPhysicalDelta(int dx, int dy, bool syncLogicalBounds)
    {
        if ((dx == 0 && dy == 0) || _outerWindowRectPhysical.IsEmpty)
            return;

        _imageScreenRectPhysical.Offset(dx, dy);
        _outerWindowRectPhysical.Offset(dx, dy);
        if (!_toolbarScreenRectPhysical.IsEmpty)
            _toolbarScreenRectPhysical.Offset(dx, dy);

        var movedTogether = false;
        if (_chromeWindow.IsVisible)
        {
            var dpi = GetAuthoritativeDpi();
            var chromeBounds = PinnedImageTranslateChromeWindow.CalculateOuterBounds(
                _imageScreenRectPhysical,
                dpi);
            movedTogether = Win32Helper.SetTwoWindowPhysicalBounds(
                this,
                _outerWindowRectPhysical,
                _chromeWindow,
                chromeBounds);
        }

        if (!movedTogether)
        {
            var dpi = GetAuthoritativeDpi();
            Win32Helper.SetWindowPhysicalBounds(
                this,
                _outerWindowRectPhysical.Left,
                _outerWindowRectPhysical.Top,
                _outerWindowRectPhysical.Width,
                _outerWindowRectPhysical.Height);

            // batch 失败时退化为顺序定位，仍保持 Main/Chrome 一致。
            if (_chromeWindow.IsVisible)
            {
                _chromeWindow.UpdateBounds(_imageScreenRectPhysical, dpi);
                _chromeWindow.EnsureShownBehind(this);
            }
        }

        if (syncLogicalBounds)
            SyncLogicalBoundsAndCorrectPhysical();
    }

    private void SyncLogicalBoundsAndCorrectPhysical()
    {
        var dpi = GetAuthoritativeDpi();
        ApplyPhysicalModelToVisualTree(dpi, moveHwnd: true);
    }

    private void UpdateChromeVisualAndBounds()
    {
        if (_isClosing ||
            !_sourceInitialized ||
            _imageScreenRectPhysical.Width <= 0 ||
            _imageScreenRectPhysical.Height <= 0)
            return;

        var dpi = GetAuthoritativeDpi();
        _chromeWindow.UpdateVisual(IsActive, _viewModel.IsShadowEnabled);
        _chromeWindow.UpdateBounds(_imageScreenRectPhysical, dpi);
        _chromeWindow.EnsureShownBehind(this);
    }

    private void ResetPointerGesture(bool releaseCapture = true)
    {
        _potentialDrag = false;
        _isDragging = false;
        _gestureOwner = GestureOwner.None;
        if (releaseCapture && IsMouseCaptured)
            ReleaseMouseCapture();
    }

    private void ShowWindowContextMenu()
    {
        var menu = CreateContextMenu();

        var toolbarItem = new MenuItem
        {
            Header = ResourceText("ImageTranslatePinnedShowToolbar", "显示工具条"),
            IsCheckable = true,
            IsChecked = _viewModel.IsToolbarVisible,
        };
        toolbarItem.Click += (_, _) => _viewModel.IsToolbarVisible = !_viewModel.IsToolbarVisible;
        menu.Items.Add(toolbarItem);

        var shadowItem = new MenuItem
        {
            Header = ResourceText("ImageTranslatePinnedWindowShadow", "窗口阴影"),
            IsCheckable = true,
            IsChecked = _viewModel.IsShadowEnabled,
        };
        shadowItem.Click += (_, _) => _viewModel.IsShadowEnabled = !_viewModel.IsShadowEnabled;
        menu.Items.Add(shadowItem);
        menu.Items.Add(new Separator());

        var closeItem = new MenuItem { Header = ResourceText("Close", "关闭") };
        closeItem.Click += (_, _) => Close();
        menu.Items.Add(closeItem);

        OpenContextMenu(menu);
    }

    private void ShowTextContextMenu()
    {
        var menu = CreateContextMenu();
        var copyItem = new MenuItem
        {
            Header = ResourceText("Copy", "复制"),
            IsEnabled = !string.IsNullOrEmpty(PART_ImageZoom.SelectedText),
        };
        copyItem.Click += (_, _) => _viewModel.CopySelectedTextCommand.Execute(PART_ImageZoom);
        menu.Items.Add(copyItem);

        var selectAllItem = new MenuItem { Header = ResourceText("SelectAll", "全选") };
        selectAllItem.Click += (_, _) => _viewModel.SelectAllTextCommand.Execute(PART_ImageZoom);
        menu.Items.Add(selectAllItem);

        OpenContextMenu(menu);
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu { PlacementTarget = PART_ImageSurface };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
                _activeContextMenu = null;
        };
        return menu;
    }

    private void OpenContextMenu(ContextMenu menu)
    {
        _activeContextMenu = menu;
        menu.IsOpen = true;
    }

    private string ResourceText(string key, string fallback) =>
        TryFindResource(key) as string ?? fallback;

    private IEnumerable<ComboBox> GetToolbarComboBoxes()
    {
        yield return PART_OcrEngineComboBox;
        yield return PART_TranslateEngineComboBox;
        yield return PART_LayoutComboBox;
        yield return PART_OcrLanguageComboBox;
        yield return PART_SourceLanguageComboBox;
        yield return PART_TargetLanguageComboBox;
    }

    private bool IsEventInsideToolbar(DependencyObject? source)
    {
        if (!_viewModel.IsToolbarVisible || source == null)
            return false;

        for (DependencyObject? current = source; current != null; current = GetVisualOrLogicalParent(current))
        {
            if (ReferenceEquals(current, PART_ToolbarBorder))
                return true;
        }

        return false;
    }

    private bool IsKeyboardFocusInsideToolbarOrMenu()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused)
            return false;

        if (focused is MenuItem or ComboBoxItem)
            return true;

        return IsEventInsideToolbar(focused);
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject current)
    {
        if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current);
        return LogicalTreeHelper.GetParent(current);
    }

    private DpiScale GetAuthoritativeDpi()
    {
        if (_sourceInitialized)
            return VisualTreeHelper.GetDpi(this);

        return Win32Helper.GetDpiScaleForPhysicalPoint(
            _imageScreenRectPhysical.Left + _imageScreenRectPhysical.Width / 2,
            _imageScreenRectPhysical.Top + _imageScreenRectPhysical.Height / 2);
    }

    private static DrawingRectangle GetPhysicalWorkArea(int physicalX, int physicalY)
    {
        var monitor = MonitorInfo.GetDisplayMonitors()
            .FirstOrDefault(item =>
            {
                var bounds = item.Bounds;
                return physicalX >= bounds.X && physicalX < bounds.X + bounds.Width &&
                       physicalY >= bounds.Y && physicalY < bounds.Y + bounds.Height;
            }) ?? MonitorInfo.GetPrimaryDisplayMonitor();

        var area = monitor.WorkingArea;
        return new DrawingRectangle(
            (int)Math.Round(area.X),
            (int)Math.Round(area.Y),
            (int)Math.Round(area.Width),
            (int)Math.Round(area.Height));
    }

    private static bool TryGetCursorPosition(out DrawingPoint point)
    {
        if (PInvoke.GetCursorPos(out var nativePoint))
        {
            point = new DrawingPoint(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
            {
                var packed = lParam.ToInt64();
                var x = unchecked((short)(packed & 0xFFFF));
                var y = unchecked((short)((packed >> 16) & 0xFFFF));
                var point = new DrawingPoint(x, y);

                var insideImage = _imageScreenRectPhysical.Contains(point);
                var insideToolbar = _viewModel.IsToolbarVisible && _toolbarScreenRectPhysical.Contains(point);
                if (!insideImage && !insideToolbar)
                {
                    handled = true;
                    return HtTransparent;
                }
                break;
            }
            case WmDpiChanged:
                // 等 WPF 更新 DPI 后按物理模型重排。
                Dispatcher.BeginInvoke(RecalculateLayout, DispatcherPriority.Loaded);
                break;
        }

        return 0;
    }

    private enum GestureOwner
    {
        None,
        Toolbar,
        Text,
        ImageContentBlank,
    }
}
