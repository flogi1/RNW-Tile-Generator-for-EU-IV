using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RnwTileGenerator.App;

public enum PaintEventKind { Down, Drag, Up, Hover }

/// <summary>
/// A zoomable, pannable canvas that displays an RGB image representing
/// "the tile" and forwards mouse events translated into image pixel
/// coordinates. It knows nothing about land/sea/rivers/provinces - the
/// active stage controller supplies the pixels (via ImageProvider) and
/// receives clicks/drags (via OnPaint). Direct port of the original Python
/// "gui/canvas_view.py" PaintCanvas (Tkinter Canvas + PIL) onto WPF's
/// WriteableBitmap.
///
/// Kept deliberately simple: only the visible viewport is composed and
/// only that crop is resized for display (via GPU-accelerated nearest-
/// neighbor scaling, same idea as PIL's Image.NEAREST resize), so this
/// stays responsive even on the largest legal tile (2304 x 2048 px).
/// </summary>
public sealed class PaintCanvasControl : Border
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 16.0;

    private readonly Canvas _host = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _errorText = new() { Foreground = Brushes.OrangeRed, Margin = new Thickness(8), Visibility = Visibility.Collapsed };
    private readonly Ellipse _brushPreview = new()
    {
        Stroke = Brushes.White,
        StrokeThickness = 1.5,
        Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    /// <summary>Given a crop rectangle in image pixel space, must return a
    /// tightly-packed RGB24 (R,G,B,R,G,B,...) row-major top-down byte
    /// buffer of exactly rect.Width*rect.Height*3 bytes.</summary>
    public Func<Int32Rect, byte[]>? ImageProvider;

    /// <summary>kind, image-space x, image-space y.</summary>
    public Action<PaintEventKind, double, double>? OnPaint;

    public double Zoom = 1.0;
    /// <summary>Image-space coordinate shown at the control's top-left.</summary>
    public double PanX;
    public double PanY;

    /// <summary>Brush radius (image pixels) to preview as a hover circle, or
    /// null to hide the preview entirely (e.g. for non-brush tools). Set by
    /// the active stage whenever its BrushSizePanel value changes.</summary>
    public double? BrushPreviewRadius
    {
        get => _brushPreviewRadius;
        set { _brushPreviewRadius = value; UpdateBrushPreview(); }
    }
    private double? _brushPreviewRadius;
    private Point? _lastScreenPos;

    private bool _panning;
    private Point _panStart;
    private (double x, double y) _panStartOffset;
    private bool _fittedOnce;
    private bool _redrawQueued;

    public PaintCanvasControl()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;

        _host.Children.Add(_image);
        _host.Children.Add(_errorText);
        _host.Children.Add(_brushPreview);
        RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);
        Child = _host;

        SizeChanged += (_, _) =>
        {
            if (!_fittedOnce && ActualWidth > 1) FitToWindow();
            else RequestRedraw();
        };

        MouseLeftButtonDown += OnLeftDown;
        MouseLeftButtonUp += OnLeftUp;
        MouseMove += OnMouseMove;
        MouseLeave += (s, e) => { _lastScreenPos = null; UpdateBrushPreview(); };
        MouseWheel += OnWheel;
        MouseDown += (s, e) => { if (e.ChangedButton is MouseButton.Middle or MouseButton.Right) OnPanStart(e); };
        MouseUp += (s, e) => { if (e.ChangedButton is MouseButton.Middle or MouseButton.Right) OnPanEnd(); };
    }

    // -- public API -----------------------------------------------------------

    public void SetImageSize(int w, int h)
    {
        // OnShow() (every stage) calls this every time its tab becomes
        // active AGAIN - including right after an Undo/Redo, which swaps in
        // a different TileProject instance but (almost always) the exact
        // same tile dimensions. Resetting the view here unconditionally
        // used to zoom/pan the user straight back out to "fit window" on
        // every single Undo/Redo, discarding whatever zoom level they were
        // actually working at (reported for the mountain brush in Runde 5,
        // "fixed" there by a keyboard-repeat guard that only masked the
        // symptom - and reported again in Runde 7 for the manual province
        // border pen, which is what exposed the real cause). Only actually
        // re-fit when the dimensions changed (a genuinely new/different
        // tile) or this is the very first time an image is shown.
        bool sizeChanged = w != ImageWidth || h != ImageHeight;
        ImageWidth = w;
        ImageHeight = h;
        if (!sizeChanged && _fittedOnce) { RequestRedraw(); return; }
        if (ActualWidth > 1) FitToWindow();
        else { _fittedOnce = false; RequestRedraw(); }
    }

    public void FitToWindow()
    {
        double cw = Math.Max(ActualWidth, 1), ch = Math.Max(ActualHeight, 1);
        Zoom = Math.Max(MinZoom, Math.Min(cw / Math.Max(ImageWidth, 1), ch / Math.Max(ImageHeight, 1)));
        PanX = 0;
        PanY = 0;
        _fittedOnce = true;
        RequestRedraw();
    }

    public void RequestRedraw()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _redrawQueued = false;
            Redraw();
        }));
    }

    public (double x, double y) ScreenToImage(double sx, double sy) => (PanX + sx / Zoom, PanY + sy / Zoom);

    // -- internals --------------------------------------------------------------

    private (int x0, int y0, int x1, int y1) VisibleRect()
    {
        double cw = Math.Max(ActualWidth, 1), ch = Math.Max(ActualHeight, 1);
        int x0 = Math.Max(0, (int)PanX);
        int y0 = Math.Max(0, (int)PanY);
        int x1 = Math.Min(ImageWidth, (int)(PanX + cw / Zoom) + 1);
        int y1 = Math.Min(ImageHeight, (int)(PanY + ch / Zoom) + 1);
        if (x1 <= x0) x1 = Math.Min(ImageWidth, x0 + 1);
        if (y1 <= y0) y1 = Math.Min(ImageHeight, y0 + 1);
        return (x0, y0, x1, y1);
    }

    private void Redraw()
    {
        if (ImageWidth <= 0 || ImageHeight <= 0 || ImageProvider == null) return;
        var (x0, y0, x1, y1) = VisibleRect();
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return;

        // The whole body (not just ImageProvider) is guarded: a mismatched
        // buffer size from a malformed/loaded tile, or any other drawing
        // hiccup, should show as an in-canvas error banner rather than
        // taking the whole app down.
        try
        {
            byte[] rgb = ImageProvider(new Int32Rect(x0, y0, w, h));
            if (rgb.Length != w * h * 3)
                throw new InvalidOperationException($"image provider returned {rgb.Length} bytes, expected {w * h * 3} ({w}x{h}x3)");

            // Reference-image overlay (Runde 9): blended in for every stage
            // here in one place, rather than each stage's own
            // ImageProvider needing to know about it - see
            // ReferenceOverlay.cs.
            ReferenceOverlay.BlendInto(rgb, x0, y0, w, h);

            _errorText.Visibility = Visibility.Collapsed;
            _image.Visibility = Visibility.Visible;

            double dispW = Math.Max(1, Math.Round(w * Zoom));
            double dispH = Math.Max(1, Math.Round(h * Zoom));

            WriteableBitmap bmp;
            if (Zoom < 1.0)
            {
                // Zoomed out: WPF's own minification (BitmapScalingMode.
                // Fant/HighQuality) needs render-tier 2 hardware to actually
                // engage - on anything below that it silently falls back to
                // nearest-neighbor point sampling, which can skip most of a
                // 1px-wide diagonal river/border entirely, reading as a
                // dashed/broken line even though the underlying data is
                // fully connected (reported as "preview still shows gaps" -
                // the gap is in this display scaling, not the river data,
                // and export uses the exact same byte buffer as this
                // preview - see TileProject.RiverRaster()). Downsampling it
                // ourselves sidesteps the GPU-tier dependency entirely.
                //
                // First attempt (a single global background color) still
                // lost a line wherever it ran near a SECOND large-area fill
                // (e.g. a river mouth near the sea): land and sea are both
                // "background" in the sense of covering big areas, but only
                // ONE of them was picked, so the other was wrongly treated
                // as a rare/thin foreground color competing with the actual
                // river for the same block - confirmed by a targeted
                // multi-line-near-a-coast test, gaps up to 18 pixels long
                // exactly like the reported screenshot. Fix: treat EVERY
                // color that covers a large share of the crop (more than
                // 1% of its pixels - i.e. any substantial fill: land, sea,
                // wasteland tint, ...) as background, not just the single
                // most common one. A block containing any OTHER (rare/thin)
                // color always shows that instead; a block that is only
                // background-like colors shows ITS OWN local majority
                // among those (so a genuine land/sea coastline still reads
                // correctly, just no longer at war with the river for the
                // same block).
                var globalCounts = new Dictionary<int, int>();
                for (int i = 0; i < w * h; i++)
                {
                    int o = i * 3;
                    int key = (rgb[o] << 16) | (rgb[o + 1] << 8) | rgb[o + 2];
                    globalCounts[key] = globalCounts.GetValueOrDefault(key) + 1;
                }
                int bgThreshold = Math.Max(1, w * h / 100);
                var bgKeys = new HashSet<int>();
                foreach (var kv in globalCounts)
                    if (kv.Value > bgThreshold) bgKeys.Add(kv.Key);

                int dw = (int)dispW, dh = (int)dispH;
                var down = new byte[dw * dh * 3];
                for (int dy = 0; dy < dh; dy++)
                {
                    int sy0 = dy * h / dh, sy1 = Math.Max(sy0 + 1, (dy + 1) * h / dh);
                    for (int dx = 0; dx < dw; dx++)
                    {
                        int sx0 = dx * w / dw, sx1 = Math.Max(sx0 + 1, (dx + 1) * w / dw);
                        int key = FeaturePreservingBlockColor(rgb, w, sx0, sy0, sx1, sy1, bgKeys);
                        int o = (dy * dw + dx) * 3;
                        down[o] = (byte)(key >> 16); down[o + 1] = (byte)(key >> 8); down[o + 2] = (byte)key;
                    }
                }
                bmp = new WriteableBitmap(dw, dh, 96, 96, PixelFormats.Rgb24, null);
                bmp.WritePixels(new Int32Rect(0, 0, dw, dh), down, dw * 3, 0);
            }
            else
            {
                // Zoomed in: plain nearest-neighbor keeps pixel edges crisp
                // for precision editing - GPU-tier independent either way.
                RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
                bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Rgb24, null);
                bmp.WritePixels(new Int32Rect(0, 0, w, h), rgb, w * 3, 0);
            }
            bmp.Freeze();
            _image.Source = bmp;
            _image.Width = dispW;
            _image.Height = dispH;

            double screenX = Math.Round((x0 - PanX) * Zoom);
            double screenY = Math.Round((y0 - PanY) * Zoom);
            Canvas.SetLeft(_image, screenX);
            Canvas.SetTop(_image, screenY);
        }
        catch (Exception ex)
        {
            _image.Visibility = Visibility.Collapsed;
            _errorText.Text = $"draw error: {ex.Message}";
            _errorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Picks the packed RGB color for one destination pixel when
    /// downsampling the source block [sx0,sx1) x [sy0,sy1): `bgKeys` (every
    /// color covering a large share of the whole crop, decided once by the
    /// caller - see Redraw()) is treated as "large-area fill" - the first
    /// pixel in the block that is NOT one of those always wins, since a
    /// thin foreground line/marker is a local minority wherever it
    /// appears, and this never lets it get skipped or outvoted the way a
    /// true area-average filter would. If the whole block is only
    /// background-like colors (e.g. it sits on a land/sea boundary), its
    /// own local majority among just those wins instead, so that boundary
    /// still reads correctly rather than always collapsing to one fixed
    /// color.</summary>
    private static int FeaturePreservingBlockColor(byte[] rgb, int srcStride, int sx0, int sy0, int sx1, int sy1, HashSet<int> bgKeys)
    {
        Dictionary<int, int>? bgLocalCounts = null;
        for (int sy = sy0; sy < sy1; sy++)
        {
            int row = sy * srcStride;
            for (int sx = sx0; sx < sx1; sx++)
            {
                int o = (row + sx) * 3;
                int key = (rgb[o] << 16) | (rgb[o + 1] << 8) | rgb[o + 2];
                if (!bgKeys.Contains(key)) return key;
                (bgLocalCounts ??= new Dictionary<int, int>())[key] = bgLocalCounts.GetValueOrDefault(key) + 1;
            }
        }
        int bestKey = -1, bestCount = -1;
        if (bgLocalCounts != null)
            foreach (var kv in bgLocalCounts)
                if (kv.Value > bestCount) { bestCount = kv.Value; bestKey = kv.Key; }
        return bestKey;
    }

    private void ZoomAt(double sx, double sy, double factor)
    {
        var (ix, iy) = ScreenToImage(sx, sy);
        Zoom = Math.Max(MinZoom, Math.Min(MaxZoom, Zoom * factor));
        PanX = ix - sx / Zoom;
        PanY = iy - sy / Zoom;
        RequestRedraw();
        UpdateBrushPreview();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        ZoomAt(pos.X, pos.Y, e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void OnPanStart(MouseButtonEventArgs e)
    {
        _panning = true;
        _panStart = e.GetPosition(this);
        _panStartOffset = (PanX, PanY);
        CaptureMouse();
        UpdateBrushPreview(); // hidden while panning
    }

    /// <summary>Positions/sizes (or hides) the brush-radius hover circle in
    /// screen space. Hidden while panning (the cursor isn't painting then),
    /// when the mouse has left the canvas, or when no stage has set a
    /// preview radius (non-brush tools, e.g. province region clicks).</summary>
    private void UpdateBrushPreview()
    {
        if (_panning || _lastScreenPos is not { } pos || _brushPreviewRadius is not { } r)
        {
            _brushPreview.Visibility = Visibility.Collapsed;
            return;
        }
        double screenRadius = Math.Max(r, 0.5) * Zoom;
        _brushPreview.Width = screenRadius * 2;
        _brushPreview.Height = screenRadius * 2;
        Canvas.SetLeft(_brushPreview, pos.X - screenRadius);
        Canvas.SetTop(_brushPreview, pos.Y - screenRadius);
        _brushPreview.Visibility = Visibility.Visible;
    }

    private void OnPanEnd()
    {
        _panning = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateBrushPreview();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        _lastScreenPos = pos; // kept valid through panning so the preview snaps back correctly
        UpdateBrushPreview();
        if (_panning)
        {
            double dx = pos.X - _panStart.X, dy = pos.Y - _panStart.Y;
            PanX = _panStartOffset.x - dx / Zoom;
            PanY = _panStartOffset.y - dy / Zoom;
            RequestRedraw();
            return;
        }
        var (ix, iy) = ScreenToImage(pos.X, pos.Y);
        if (e.LeftButton == MouseButtonState.Pressed)
            OnPaint?.Invoke(PaintEventKind.Drag, ix, iy);
        else
            OnPaint?.Invoke(PaintEventKind.Hover, ix, iy);
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        var pos = e.GetPosition(this);
        var (ix, iy) = ScreenToImage(pos.X, pos.Y);
        OnPaint?.Invoke(PaintEventKind.Down, ix, iy);
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        var pos = e.GetPosition(this);
        var (ix, iy) = ScreenToImage(pos.X, pos.Y);
        OnPaint?.Invoke(PaintEventKind.Up, ix, iy);
    }
}
