using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIUsageMonitor;

internal sealed class ProviderDragAdorner : Adorner
{
    private static readonly Brush ShadowBrush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
    private static readonly Pen OutlinePen = new(new SolidColorBrush(Color.FromArgb(210, 124, 92, 252)), 1.5);

    private readonly ImageSource _snapshot;
    private readonly Size _cardSize;
    private readonly Point _grabOffset;
    private Point _cursorPosition;

    public ProviderDragAdorner(UIElement adornedElement, FrameworkElement card, Point grabOffset)
        : base(adornedElement)
    {
        _snapshot = Capture(card);
        _cardSize = new Size(card.ActualWidth, card.ActualHeight);
        _grabOffset = grabOffset;
        IsHitTestVisible = false;
    }

    public void UpdatePosition(Point cursorPosition)
    {
        _cursorPosition = cursorPosition;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        Rect cardBounds = new(
            _cursorPosition.X - _grabOffset.X,
            _cursorPosition.Y - _grabOffset.Y,
            _cardSize.Width,
            _cardSize.Height);
        Rect shadowBounds = cardBounds;
        shadowBounds.Offset(0, 5);

        drawingContext.DrawRoundedRectangle(ShadowBrush, null, shadowBounds, 12, 12);
        drawingContext.PushOpacity(0.94);
        drawingContext.DrawImage(_snapshot, cardBounds);
        drawingContext.Pop();
        drawingContext.DrawRoundedRectangle(null, OutlinePen, cardBounds, 12, 12);
    }

    private static ImageSource Capture(FrameworkElement card)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(card);
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY));
        RenderTargetBitmap bitmap = new(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        return bitmap;
    }
}
