using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FiltSyn.Core;

namespace FiltSyn.App;

/// <summary>Draws a <see cref="HairpinLayout"/> to scale with port labels and a scale bar.</summary>
public sealed class LayoutView : Control
{
    private FilterLayout? _layout;

    public FilterLayout? Layout
    {
        get => _layout;
        set { _layout = value; InvalidateVisual(); }
    }

    private static readonly IBrush Copper = new SolidColorBrush(Color.FromRgb(0xC8, 0x7F, 0x2E));
    private static readonly IBrush BoardFill = new SolidColorBrush(Color.FromRgb(0x1F, 0x4D, 0x3A));
    private static readonly Pen BoardPen = new(new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0xA5)), 1);
    private static readonly Pen CopperPen = new(new SolidColorBrush(Color.FromRgb(0xF0, 0xB8, 0x6E)), 1);

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var size = Bounds.Size;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1C)), null, new Avalonia.Rect(size));
        if (_layout is null || size.Width < 20 || size.Height < 20)
        {
            Text(ctx, "Synthesize a design to see its layout.", new Point(16, 16), 13, Brushes.Gray);
            return;
        }

        var b = _layout.Board;
        const double margin = 40;
        double scale = Math.Min((size.Width - 2 * margin) / b.Width, (size.Height - 2 * margin - 20) / b.Height);
        double ox = (size.Width - b.Width * scale) / 2, oy = (size.Height - 20 - b.Height * scale) / 2;
        Point Map(double x, double y) => new(ox + (x - b.X0) * scale, oy + (b.Y1 - y) * scale);

        // Board first so the copper is painted on top of it.
        foreach (var poly in _layout.Polygons.OrderBy(q => q.Layer == FilterLayout.BoardLayer ? 0 : 1))
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Map(poly.Points[0].X, poly.Points[0].Y), true);
                for (int i = 1; i < poly.Points.Length; i++) g.LineTo(Map(poly.Points[i].X, poly.Points[i].Y));
                g.EndFigure(true);
            }
            if (poly.Layer == FilterLayout.BoardLayer) ctx.DrawGeometry(BoardFill, BoardPen, geo);
            else ctx.DrawGeometry(Copper, CopperPen, geo);
        }

        for (int i = 0; i < _layout.Ports.Count; i++)
        {
            var p = Map(_layout.Ports[i].X, _layout.Ports[i].Y);
            ctx.DrawEllipse(Brushes.White, null, p, 4, 4);
            Text(ctx, $"P{i + 1}", new Point(p.X + 8, p.Y - 18), 13, Brushes.White);
        }

        // Scale bar: pick a round length near 20% of the board width.
        double target = b.Width * 0.2 * 1e3;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double mm = new[] { 1.0, 2.0, 5.0, 10.0 }.Select(m => m * mag).OrderBy(v => Math.Abs(v - target)).First();
        double px = mm * 1e-3 * scale, y = size.Height - 24, x = margin;
        var pen = new Pen(Brushes.White, 2);
        ctx.DrawLine(pen, new Point(x, y), new Point(x + px, y));
        ctx.DrawLine(pen, new Point(x, y - 4), new Point(x, y + 4));
        ctx.DrawLine(pen, new Point(x + px, y - 4), new Point(x + px, y + 4));
        Text(ctx, $"{mm:0.##} mm", new Point(x + px + 8, y - 9), 12, Brushes.White);
        Text(ctx, $"Board {b.Width * 1e3:F1} x {b.Height * 1e3:F1} mm", new Point(size.Width - 200, y - 9), 12, Brushes.LightGray);
    }

    private static void Text(DrawingContext ctx, string s, Point at, double sz, IBrush brush)
        => ctx.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, sz, brush), at);
}
