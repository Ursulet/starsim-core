using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
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
    public static readonly StyledProperty<bool> IsClippingWarningEnabledProperty = AvaloniaProperty.Register<ImageComparisonViewer, bool>(nameof(IsClippingWarningEnabled));
    public static readonly StyledProperty<bool> IsRoiSelectionEnabledProperty = AvaloniaProperty.Register<ImageComparisonViewer, bool>(nameof(IsRoiSelectionEnabled));
    public static readonly StyledProperty<Rect> RoiImageRectProperty = AvaloniaProperty.Register<ImageComparisonViewer, Rect>(nameof(RoiImageRect), defaultBindingMode: BindingMode.TwoWay);

    private Vector pan;
    private Point pointerStart;
    private Vector panStart;
    private bool isPanning;
    private bool isDraggingSplit;
    private bool isSelectingRoi;
    private Point roiAnchor;
    private Point roiCurrent;
    private byte[]? clippingOverlaySource;
    private Bitmap? clippingOverlay;
    private Bitmap? lastRenderedOriginal;

    static ImageComparisonViewer() =>
        AffectsRender<ImageComparisonViewer>(
            OriginalProperty,
            ProcessedProperty,
            ProcessedPixelsProperty,
            FitProperty,
            ZoomProperty,
            CompareProperty,
            PreviewEnabledProperty,
            SplitProperty,
            IsClippingWarningEnabledProperty,
            IsRoiSelectionEnabledProperty,
            RoiImageRectProperty);

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
    public bool IsClippingWarningEnabled { get => GetValue(IsClippingWarningEnabledProperty); set => SetValue(IsClippingWarningEnabledProperty, value); }
    public bool IsRoiSelectionEnabled { get => GetValue(IsRoiSelectionEnabledProperty); set => SetValue(IsRoiSelectionEnabledProperty, value); }
    public Rect RoiImageRect { get => GetValue(RoiImageRectProperty); set => SetValue(RoiImageRectProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#05080C")), Bounds);
        if (Original is null)
        {
            return;
        }
        if (!ReferenceEquals(lastRenderedOriginal, Original))
        {
            lastRenderedOriginal = Original;
            pan = default;
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

        DrawClippingWarning(context, source, destination);
        DrawRoiSelection(context, destination);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Original is null) return;
        var fitScale = Math.Min(
            Bounds.Width / Math.Max(1, Original.Size.Width),
            Bounds.Height / Math.Max(1, Original.Size.Height));
        var currentScale = Fit ? fitScale : Zoom;
        Fit = false;
        Zoom = Math.Clamp(currentScale * (e.Delta.Y > 0 ? 1.25 : 0.8), 0.25, 16);
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
        if (IsRoiSelectionEnabled && GetImageRect(Original).Contains(point))
        {
            isSelectingRoi = true;
            roiAnchor = point;
            roiCurrent = point;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }
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
        else if (isSelectingRoi)
        {
            roiCurrent = point;
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
        if (isSelectingRoi)
        {
            roiCurrent = e.GetPosition(this);
            CommitRoiSelection();
        }
        isPanning = false;
        isDraggingSplit = false;
        isSelectingRoi = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        clippingOverlay?.Dispose();
        clippingOverlay = null;
        clippingOverlaySource = null;
        base.OnDetachedFromVisualTree(e);
    }

    private Rect GetImageRect(Bitmap bitmap)
    {
        var fitScale = Math.Min(Bounds.Width / Math.Max(1, bitmap.Size.Width), Bounds.Height / Math.Max(1, bitmap.Size.Height));
        var scale = Fit ? fitScale : Zoom;
        var size = bitmap.Size * scale;
        return new Rect((Bounds.Width - size.Width) / 2 + pan.X, (Bounds.Height - size.Height) / 2 + pan.Y, size.Width, size.Height);
    }

    private void DrawClippingWarning(DrawingContext context, Rect source, Rect destination)
    {
        if (!IsClippingWarningEnabled)
        {
            clippingOverlay?.Dispose();
            clippingOverlay = null;
            clippingOverlaySource = null;
            return;
        }
        if (!PreviewEnabled || Processed is null || ProcessedPixels is null)
            return;

        EnsureClippingOverlay();
        if (clippingOverlay is null) return;
        if (Compare)
        {
            var divider = Math.Clamp(Bounds.Width * Split, 0, Bounds.Width);
            using (context.PushClip(new Rect(divider, 0, Bounds.Width - divider, Bounds.Height)))
                context.DrawImage(clippingOverlay, source, destination);
        }
        else
        {
            context.DrawImage(clippingOverlay, source, destination);
        }
    }

    private void EnsureClippingOverlay()
    {
        if (Processed is null || ProcessedPixels is null) return;
        if (ReferenceEquals(clippingOverlaySource, ProcessedPixels) && clippingOverlay is not null) return;

        clippingOverlay?.Dispose();
        clippingOverlay = null;
        clippingOverlaySource = ProcessedPixels;
        var width = Processed.PixelSize.Width;
        var height = Processed.PixelSize.Height;
        if (ProcessedPixels.Length < checked(width * height * 4)) return;

        var overlayPixels = new byte[checked(width * height * 4)];
        for (var offset = 0; offset < overlayPixels.Length; offset += 4)
        {
            if (ProcessedPixels[offset] < byte.MaxValue &&
                ProcessedPixels[offset + 1] < byte.MaxValue &&
                ProcessedPixels[offset + 2] < byte.MaxValue)
                continue;

            // Premultiplied BGRA: vivid red at 72% opacity.
            overlayPixels[offset + 2] = 184;
            overlayPixels[offset + 3] = 184;
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var framebuffer = bitmap.Lock())
        {
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    overlayPixels,
                    row * width * 4,
                    framebuffer.Address + row * framebuffer.RowBytes,
                    width * 4);
            }
        }
        clippingOverlay = bitmap;
    }

    private void DrawRoiSelection(DrawingContext context, Rect imageRect)
    {
        Rect selection;
        if (isSelectingRoi)
        {
            selection = NormalizeAndClamp(roiAnchor, roiCurrent, imageRect);
        }
        else if (RoiImageRect.Width > 0 && RoiImageRect.Height > 0 && Original is not null)
        {
            var scaleX = imageRect.Width / Original.PixelSize.Width;
            var scaleY = imageRect.Height / Original.PixelSize.Height;
            selection = new Rect(
                imageRect.X + RoiImageRect.X * scaleX,
                imageRect.Y + RoiImageRect.Y * scaleY,
                RoiImageRect.Width * scaleX,
                RoiImageRect.Height * scaleY);
        }
        else
        {
            return;
        }

        var shade = new SolidColorBrush(Color.Parse("#78000000"));
        if (selection.Top > imageRect.Top)
            context.FillRectangle(shade, new Rect(imageRect.Left, imageRect.Top, imageRect.Width, selection.Top - imageRect.Top));
        if (selection.Bottom < imageRect.Bottom)
            context.FillRectangle(shade, new Rect(imageRect.Left, selection.Bottom, imageRect.Width, imageRect.Bottom - selection.Bottom));
        if (selection.Left > imageRect.Left)
            context.FillRectangle(shade, new Rect(imageRect.Left, selection.Top, selection.Left - imageRect.Left, selection.Height));
        if (selection.Right < imageRect.Right)
            context.FillRectangle(shade, new Rect(selection.Right, selection.Top, imageRect.Right - selection.Right, selection.Height));
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#35B6FF")), 2), selection);
    }

    private void CommitRoiSelection()
    {
        if (Original is null) return;
        var imageRect = GetImageRect(Original);
        var selection = NormalizeAndClamp(roiAnchor, roiCurrent, imageRect);
        if (selection.Width < 3 || selection.Height < 3) return;

        var scaleX = Original.PixelSize.Width / imageRect.Width;
        var scaleY = Original.PixelSize.Height / imageRect.Height;
        var left = Math.Clamp((int)Math.Floor((selection.Left - imageRect.Left) * scaleX), 0, Original.PixelSize.Width - 1);
        var top = Math.Clamp((int)Math.Floor((selection.Top - imageRect.Top) * scaleY), 0, Original.PixelSize.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling((selection.Right - imageRect.Left) * scaleX), left + 1, Original.PixelSize.Width);
        var bottom = Math.Clamp((int)Math.Ceiling((selection.Bottom - imageRect.Top) * scaleY), top + 1, Original.PixelSize.Height);
        RoiImageRect = new Rect(left, top, right - left, bottom - top);
    }

    private static Rect NormalizeAndClamp(Point first, Point second, Rect bounds)
    {
        var left = Math.Clamp(Math.Min(first.X, second.X), bounds.Left, bounds.Right);
        var top = Math.Clamp(Math.Min(first.Y, second.Y), bounds.Top, bounds.Bottom);
        var right = Math.Clamp(Math.Max(first.X, second.X), bounds.Left, bounds.Right);
        var bottom = Math.Clamp(Math.Max(first.Y, second.Y), bounds.Top, bounds.Bottom);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
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
