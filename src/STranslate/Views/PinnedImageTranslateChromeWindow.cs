using STranslate.Helpers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DrawingRectangle = System.Drawing.Rectangle;

namespace STranslate.Views;

/// <summary>
/// 为 Pinned 内容窗口绘制阴影与激活辉光的伴随窗。
/// </summary>
internal sealed class PinnedImageTranslateChromeWindow : Window
{
    // 为阴影与辉光预留的外围区域。
    private const double ChromeMarginDip = 10;

    private static readonly Color ActiveGlowColor = Color.FromRgb(0x4D, 0x90, 0xFE);

    private readonly Border _shadowCaster;
    private readonly Border _activeGlowCaster;
    private bool _isActive;
    private bool _isShadowEnabled = true;
    private bool _sourceInitialized;
    private bool _isClosed;
    private bool _captureCloaked;

    internal PinnedImageTranslateChromeWindow()
    {
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;

        _shadowCaster = new Border
        {
            Margin = new Thickness(ChromeMarginDip),
            Background = Brushes.White,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                Direction = 0,
                Opacity = 0.36,
                ShadowDepth = 0,
                RenderingBias = RenderingBias.Performance,
                Color = Colors.Black,
            },
        };

        var activeGlowBrush = new SolidColorBrush(ActiveGlowColor);
        activeGlowBrush.Freeze();
        _activeGlowCaster = new Border
        {
            Margin = new Thickness(ChromeMarginDip),
            Background = activeGlowBrush,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect
            {
                BlurRadius = 6,
                Direction = 0,
                Opacity = 0.42,
                ShadowDepth = 0,
                RenderingBias = RenderingBias.Quality,
                Color = ActiveGlowColor,
            },
        };

        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(_shadowCaster);
        root.Children.Add(_activeGlowCaster);
        Content = root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _sourceInitialized = true;
        Win32Helper.HideFromAltTab(this);
        Win32Helper.ConfigureClickThroughNoActivate(this);
        if (_captureCloaked && !Win32Helper.SetWindowCloaked(this, true))
            throw new InvalidOperationException("Failed to cloak the pinned window chrome during capture.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        base.OnClosed(e);
    }

    internal void UpdateVisual(bool isActive, bool isShadowEnabled)
    {
        if (_isClosed)
            return;

        _isActive = isActive;
        _isShadowEnabled = isShadowEnabled;
        _activeGlowCaster.Visibility = _isActive ? Visibility.Visible : Visibility.Collapsed;
        _shadowCaster.Visibility = !_isActive && _isShadowEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateVisibility();
    }

    internal void UpdateBounds(DrawingRectangle imageBounds, DpiScale dpi)
    {
        if (_isClosed || imageBounds.Width <= 0 || imageBounds.Height <= 0)
            return;

        var outer = CalculateOuterBounds(imageBounds, dpi);
        var sx = Math.Max(0.01, dpi.DpiScaleX);
        var sy = Math.Max(0.01, dpi.DpiScaleY);

        Left = outer.Left / sx;
        Top = outer.Top / sy;
        Width = outer.Width / sx;
        Height = outer.Height / sy;

        Win32Helper.SetWindowPhysicalBounds(
            this,
            outer.Left,
            outer.Top,
            outer.Width,
            outer.Height,
            showWindow: false);

        UpdateVisibility();
    }

    internal static DrawingRectangle CalculateOuterBounds(DrawingRectangle imageBounds, DpiScale dpi)
    {
        var sx = Math.Max(0.01, dpi.DpiScaleX);
        var sy = Math.Max(0.01, dpi.DpiScaleY);
        var marginX = Math.Max(1, (int)Math.Ceiling(ChromeMarginDip * sx));
        var marginY = Math.Max(1, (int)Math.Ceiling(ChromeMarginDip * sy));
        return DrawingRectangle.FromLTRB(
            imageBounds.Left - marginX,
            imageBounds.Top - marginY,
            imageBounds.Right + marginX,
            imageBounds.Bottom + marginY);
    }

    internal void EnsureShownBehind(Window contentWindow)
    {
        if (_isClosed || !ShouldBeVisible)
            return;

        if (!IsVisible)
            Show();

        Win32Helper.PlaceWindowBehind(this, contentWindow);
    }

    internal bool SetCloaked(bool cloaked)
    {
        _captureCloaked = cloaked;
        return _isClosed || !_sourceInitialized || Win32Helper.SetWindowCloaked(this, cloaked);
    }

    internal void HideForOwnerClosing()
    {
        if (!_isClosed && IsVisible)
            Hide();
    }

    internal void CloseSafely()
    {
        if (!_isClosed)
            Close();
    }

    private bool ShouldBeVisible => _isActive || _isShadowEnabled;

    private void UpdateVisibility()
    {
        if (_isClosed || !_sourceInitialized)
            return;

        if (ShouldBeVisible)
        {
            if (!IsVisible)
                Show();
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

}
