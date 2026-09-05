using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

// 来自优化前的 Chrome 绘制，作为独立的逐像素对照。
internal static class ChromeReference
{
    internal static Grid Create(bool active)
    {
        var glow = Color.FromRgb(0x4D, 0x90, 0xFE);
        var grid = new Grid { IsHitTestVisible = false };
        grid.Children.Add(Caster(Colors.White, Colors.Black, 8, .36, RenderingBias.Performance, !active));
        grid.Children.Add(Caster(glow, glow, 6, .42, RenderingBias.Quality, active));
        return grid;
    }

    private static Border Caster(Color background, Color color, double radius, double opacity, RenderingBias bias, bool visible) => new()
    {
        Margin = new Thickness(10), Background = new SolidColorBrush(background), IsHitTestVisible = false,
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
        Effect = new DropShadowEffect { Color = color, BlurRadius = radius, Opacity = opacity,
            Direction = 0, ShadowDepth = 0, RenderingBias = bias }
    };
}
