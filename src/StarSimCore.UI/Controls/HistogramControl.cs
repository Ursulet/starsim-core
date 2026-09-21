using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StarSimCore.Application.Processing;

namespace StarSimCore.UI.Controls;

public sealed class HistogramControl : Control
{
    public static readonly StyledProperty<HistogramData?> DataProperty =
        AvaloniaProperty.Register<HistogramControl, HistogramData?>(nameof(Data));

    static HistogramControl() => AffectsRender<HistogramControl>(DataProperty);

    public HistogramData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#080D12")), Bounds);
        for (var row = 1; row < 4; row++)
        {
            var y = Bounds.Height * row / 4;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#16232E"))), new Point(0, y), new Point(Bounds.Width, y));
        }
        var data = Data;
        if (data is null) return;
        DrawSeries(context, data.RedOrGray, data.ChannelCount == 1 ? Color.Parse("#D6DEE5") : Color.Parse("#F06070"));
        if (data.ChannelCount == 3)
        {
            DrawSeries(context, data.Green, Color.Parse("#42C98A"));
            DrawSeries(context, data.Blue, Color.Parse("#4FA8FF"));
        }
    }

    private void DrawSeries(DrawingContext context, ulong[] bins, Color color)
    {
        if (bins.Length < 2) return;
        var maximum = bins.Max();
        if (maximum == 0) return;
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(0, Bounds.Height), false);
            for (var index = 0; index < bins.Length; index++)
            {
                var x = index / (double)(bins.Length - 1) * Bounds.Width;
                var normalized = Math.Log10(1 + bins[index]) / Math.Log10(1 + maximum);
                var y = Bounds.Height - normalized * (Bounds.Height - 4);
                drawing.LineTo(new Point(x, y));
            }
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.2), geometry);
    }
}
