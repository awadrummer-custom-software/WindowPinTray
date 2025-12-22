using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace WindowPinTray.ElevatedHelper;

internal sealed class OverlayWindow : Window
{
    private readonly IntPtr _targetHwnd;
    private readonly System.Windows.Controls.Button _button;
    private readonly System.Windows.Controls.Image _image;
    private IntPtr _windowHandle;

    private ImageSource? _defaultImage;
    private ImageSource? _hoverImage;
    private ImageSource? _pinnedImage;
    private ImageSource? _pinnedHoverImage;

    private HelperSettings _settings;
    private bool _isPinned;
    private bool _isMouseOver;
    private NativeMethods.RECT _lastBounds;

    public OverlayWindow(IntPtr targetHwnd, HelperSettings settings)
    {
        _targetHwnd = targetHwnd;
        _settings = settings;

        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        Topmost = true;
        Focusable = false;
        ShowActivated = false;

        _image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Fill,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = true
        };

        _button = new System.Windows.Controls.Button
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Content = _image
        };

        _button.MouseEnter += (_, _) => { _isMouseOver = true; UpdateImageSource(); };
        _button.MouseLeave += (_, _) => { _isMouseOver = false; UpdateImageSource(); };
        _button.Click += (_, _) => PinRequested?.Invoke(this, EventArgs.Empty);
        _button.MouseRightButtonDown += (_, e) => { e.Handled = true; };
        _button.MouseRightButtonUp += (_, e) => { e.Handled = true; };

        Content = _button;

        SourceInitialized += HandleSourceInitialized;
        Loaded += (_, _) => ApplySettings(_settings);
    }

    public event EventHandler? PinRequested;

    public void ApplySettings(HelperSettings settings)
    {
        _settings = settings;
        ApplyPositionAndSize();
        LoadImages();
        UpdateImageSource();
    }

    public void UpdatePinnedState(bool isPinned)
    {
        _isPinned = isPinned;
        UpdateImageSource();
    }

    public void UpdatePosition(NativeMethods.RECT bounds)
    {
        _lastBounds = bounds;
        ApplyPositionAndSize();
    }

    private void ApplyPositionAndSize()
    {
        var dpi = GetDpiScale();
        var windowWidth = _lastBounds.Right - _lastBounds.Left;
        var windowHeight = _lastBounds.Bottom - _lastBounds.Top;

        if (windowWidth <= 0 || windowHeight <= 0) return;

        var buttonWidthPixels = Math.Min(_settings.ButtonWidth, windowWidth);
        var buttonHeightPixels = Math.Min(_settings.ButtonHeight, windowHeight);

        Width = buttonWidthPixels / dpi;
        Height = buttonHeightPixels / dpi;
        _button.Width = Width;
        _button.Height = Height;

        int leftPixels = _settings.CenterButton
            ? _lastBounds.Left + (windowWidth - buttonWidthPixels) / 2 + _settings.ButtonOffsetX
            : _lastBounds.Right - _settings.ButtonOffsetX - buttonWidthPixels;

        int topPixels = _lastBounds.Top + _settings.ButtonOffsetY;

        Left = leftPixels / dpi;
        Top = topPixels / dpi;

        // Ensure TOPMOST since this is for elevated windows
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _windowHandle,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void LoadImages()
    {
        _defaultImage = LoadImage(_settings.ButtonImagePath) ?? CreateFallbackImage(Colors.White, Colors.Transparent);
        _hoverImage = LoadImage(_settings.ButtonHoverImagePath) ?? _defaultImage;
        _pinnedImage = LoadImage(_settings.ButtonPinnedImagePath);
        _pinnedHoverImage = LoadImage(_settings.ButtonPinnedHoverImagePath);

        var highlightColor = ParseColor(_settings.PinnedHighlightColor) ?? Colors.LightSkyBlue;
        _pinnedImage ??= CreateFallbackImage(highlightColor, Color.FromArgb(40, 0, 0, 0));
        _pinnedHoverImage ??= _pinnedImage;
    }

    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    private void UpdateImageSource()
    {
        _image.Source = (_isPinned, _isMouseOver) switch
        {
            (true, true) => _pinnedHoverImage ?? _pinnedImage ?? _defaultImage,
            (true, false) => _pinnedImage ?? _defaultImage,
            (false, true) => _hoverImage ?? _defaultImage,
            _ => _defaultImage
        };
    }

    private void HandleSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _windowHandle = source.Handle;

            var styles = NativeMethods.GetWindowLong(_windowHandle, NativeMethods.GWL_EXSTYLE);
            styles |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            NativeMethods.SetWindowLong(_windowHandle, NativeMethods.GWL_EXSTYLE, styles);

            source.AddHook(WndProc);
            ApplyPositionAndSize();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private double GetDpiScale()
    {
        if (_targetHwnd != IntPtr.Zero)
        {
            try
            {
                var dpi = NativeMethods.GetDpiForWindow(_targetHwnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch { }
        }
        return VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    private static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var settings = new WpfDrawingSettings { IncludeRuntime = true, TextAsGeometry = false };
                var drawing = new FileSvgReader(settings).Read(path);
                if (drawing != null)
                {
                    var img = new DrawingImage(drawing);
                    img.Freeze();
                    return img;
                }
            }
            else
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
        catch { }

        return null;
    }

    private static ImageSource CreateFallbackImage(Color strokeColor, Color fillColor)
    {
        var geometry = Geometry.Parse("M12,2 L8,6 L9,7 L7,9 L9,11 L11,9 L12,10 L16,6 z");
        var strokeBrush = new SolidColorBrush(strokeColor);
        strokeBrush.Freeze();
        var fillBrush = new SolidColorBrush(fillColor);
        fillBrush.Freeze();

        var drawingGroup = new DrawingGroup();
        drawingGroup.Children.Add(new GeometryDrawing(fillBrush, new Pen(strokeBrush, 1.4), geometry));
        drawingGroup.Freeze();

        return new DrawingImage(drawingGroup);
    }
}
