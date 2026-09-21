using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StarSimCore.Application.Localization;

namespace StarSimCore.UI.Controls;

public sealed class ImageComparisonViewer : Control
{
    public static readonly StyledProperty<Bitmap?> OriginalProperty = AvaloniaProperty.Register<ImageComparisonViewer, Bitmap?>(nameof(Original));
    public static readonly StyledProperty<Bitmap?> ProcessedProperty = AvaloniaProperty.Register<ImageComparisonViewer, Bitmap?>(nameof(Processed));
    public static readonly StyledProperty<byte[]?> OriginalPixelsProperty = AvaloniaProperty.Register<ImageComparisonViewer, byte[]?>(nameof(OriginalPixels));
    public static readonly StyledProperty<byte[]?> ProcessedPixelsProperty = AvaloniaProperty.Register<ImageComparisonViewer, byte[]?>(nameof(ProcessedPixels));
    public static readonly StyledProperty<bool> FitProperty = AvaloniaProperty.Register<ImageComparisonViewer, bool>(nameof(Fit), true, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<ImageComparisonViewer, double>(nameof(Zoom), 1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> CompareProperty = AvaloniaProperty.Register<ImageComparisonViewer, bool>(nameof(Compare));
    public static readonly StyledProperty<bool> PreviewEnabledProperty = AvaloniaProperty.Register<ImageComparisonViewer, bool>(nameof(PreviewEnabled), true);
    public static readonly StyledProperty<double> SplitProperty = AvaloniaProperty.Register<ImageComparisonViewer, double>(nameof(Split), 0.5, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> PixelReadoutProperty = AvaloniaProperty.Register<ImageComparisonViewer, string>(nameof(PixelReadout), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<uint> ChannelCountProperty = AvaloniaProperty.Register<ImageComparisonViewer, uint>(nameof(ChannelCount), 3);

    private Vector pan;
    private Point pointerStart;
    private Vector panStart;
    private bool isPanning;
    private bool isDraggingSplit;

    static ImageComparisonViewer() =>
        AffectsRender<ImageComparisonViewer>(OriginalProperty, ProcessedProperty, FitProperty, ZoomProperty, CompareProperty, PreviewEnabledProperty, SplitProperty);

    public Bitmap? Original { get => GetValue(OriginalProperty); set => SetValue(OriginalProperty, value); }
    public Bitmap? Processed { get => GetValue(ProcessedProperty); set => SetValue(ProcessedProperty, value); }
    public byte[]? OriginalPixels { get => GetValue(OriginalPixelsProperty); set => SetValue(OriginalPixelsProperty, value); }
    public byte[]? ProcessedPixels { get => GetValue(ProcessedPixelsProperty); set => SetValue(ProcessedPixelsProperty, value); }
    public bool Fit { get => GetValue(FitProperty); set => SetValue(FitProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool Compare { get => GetValue(CompareProperty); set => SetValue(CompareProperty, value); }
    public bool PreviewEnabled { get => GetValue(PreviewEnabledProperty); set => SetValue(PreviewEnabledProperty, value); }
    public double Split { get => GetValue(SplitProperty); set => SetValue(SplitProperty, value); }
    public string PixelReadout { get => GetValue(PixelReadoutProperty); set => SetValue(PixelReadoutProperty, value); }
    public uint ChannelCount { get => GetValue(ChannelCountProperty); set => SetValue(ChannelCountProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#05080C")), Bounds);
        if (Original is null)
        {
            return;
        }

        var destination = GetImageRect(Original);
        var source = new Rect(Original.Size);
        context.DrawImage(Original, source, destination);
        if (PreviewEnabled && Processed is not null && !Compare)
        {
            context.DrawImage(Processed, source, destination);
        }
        else if (PreviewEnabled && Compare && Processed is not null)
        {
            var divider = Math.Clamp(Bounds.Width * Split, 0, Bounds.Width);
            using (context.PushClip(new Rect(divider, 0, Bounds.Width - divider, Bounds.Height)))
            {
                context.DrawImage(Processed, source, destination);
            }
            context.DrawLine(new Pen(Brushes.White, 2), new Point(divider, 0), new Point(divider, Bounds.Height));
            var center = new Point(divider, Bounds.Height / 2);
            context.DrawEllipse(Brushes.White, null, center, 16, 16);
            var handlePen = new Pen(new SolidColorBrush(Color.Parse("#142333")), 1.6);
            context.DrawLine(handlePen, new Point(divider - 7, center.Y), new Point(divider - 2, center.Y - 5));
            context.DrawLine(handlePen, new Point(divider - 7, center.Y), new Point(divider - 2, center.Y + 5));
            context.DrawLine(handlePen, new Point(divider + 7, center.Y), new Point(divider + 2, center.Y - 5));
            context.DrawLine(handlePen, new Point(divider + 7, center.Y), new Point(divider + 2, center.Y + 5));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Original is null) return;
        Fit = false;
        Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.25 : 0.8), 0.25, 4);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // The secondary release opens the menu explicitly. Do not begin pan or
        // capture the pointer for a context click.
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
            return;
        base.OnPointerPressed(e);
        if (Original is null) return;
        var point = e.GetPosition(this);
        isDraggingSplit = Compare && Math.Abs(point.X - Bounds.Width * Split) <= 12;
        isPanning = !isDraggingSplit;
        pointerStart = point;
        panStart = pan;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (isDraggingSplit)
        {
            Split = Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0.02, 0.98);
            InvalidateVisual();
        }
        else if (isPanning)
        {
            pan = panStart + (point - pointerStart);
            InvalidateVisual();
        }
        UpdatePixelReadout(point);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // Open explicitly on the actual hit-tested viewer. This avoids relying
        // on a parent container or platform-specific ContextRequested routing.
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            if (ContextMenu is { } contextMenu)
            {
                contextMenu.DataContext = DataContext;
                contextMenu.Open(this);
            }
            e.Handled = true;
            return;
        }
        base.OnPointerReleased(e);
        isPanning = false;
        isDraggingSplit = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private Rect GetImageRect(Bitmap bitmap)
    {
        var fitScale = Math.Min(Bounds.Width / Math.Max(1, bitmap.Size.Width), Bounds.Height / Math.Max(1, bitmap.Size.Height));
        var scale = Fit ? fitScale : Zoom;
        var size = bitmap.Size * scale;
        return new Rect((Bounds.Width - size.Width) / 2 + pan.X, (Bounds.Height - size.Height) / 2 + pan.Y, size.Width, size.Height);
    }

    private void UpdatePixelReadout(Point point)
    {
        if (Original is null || OriginalPixels is null) return;
        var imageRect = GetImageRect(Original);
        if (!imageRect.Contains(point))
        {
            PixelReadout = LocalizationService.Instance["Viewer.OutsideImage"];
            return;
        }
        var x = Math.Clamp((int)((point.X - imageRect.X) / imageRect.Width * Original.PixelSize.Width), 0, Original.PixelSize.Width - 1);
        var y = Math.Clamp((int)((point.Y - imageRect.Y) / imageRect.Height * Original.PixelSize.Height), 0, Original.PixelSize.Height - 1);
        var useProcessed = Compare && point.X >= Bounds.Width * Split && ProcessedPixels is not null;
        var pixels = useProcessed ? ProcessedPixels! : OriginalPixels;
        var offset = (y * Original.PixelSize.Width + x) * 4;
        var valueText = ChannelCount == 1
            ? $"{LocalizationService.Instance["Viewer.Gray"]} {pixels[offset]}"
            : $"R {pixels[offset + 2]}  G {pixels[offset + 1]}  B {pixels[offset]}";
        var side = LocalizationService.Instance[useProcessed ? "Viewer.Processed" : "Viewer.Original"];
        PixelReadout = $"x {x}  y {y}  •  {valueText}  •  {side}";
    }
}
