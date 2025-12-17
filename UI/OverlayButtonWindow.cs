using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowPinTray.Interop;
using WindowPinTray.Models;

namespace WindowPinTray.UI;

internal sealed class OverlayButtonWindow : Window
{
    private readonly IntPtr _targetHwnd;
    private readonly System.Windows.Controls.Button _button;
    private readonly System.Windows.Controls.Image _image;
    private IntPtr _windowHandle;

    private ImageSource? _defaultImage;
    private ImageSource? _hoverImage;
    private ImageSource? _pinnedImage;

    private AppSettings _settings;
    private bool _isPinned;
    private bool _isMouseOver;

    public OverlayButtonWindow(IntPtr targetHwnd, AppSettings settings)
    {
        _targetHwnd = targetHwnd;
        _settings = settings.Clone();

        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        Focusable = false;

        _image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            SnapsToDevicePixels = true
        };

        _button = new System.Windows.Controls.Button
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Content = _image
        };

        _button.MouseEnter += (_, _) =>
        {
            _isMouseOver = true;
            UpdateImageSource();
        };

        _button.MouseLeave += (_, _) =>
        {
            _isMouseOver = false;
            UpdateImageSource();
        };
        _button.Click += (_, e) =>
        {
            // Shift+Click to log window info
            if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Shift)
            {
                Services.DebugLogger.LogWindowInfo(_targetHwnd, "SHIFT+CLICK on pin button");
                e.Handled = true;
            }
            else
            {
                PinRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _button.MouseRightButtonDown += (_, _) =>
        {
            Services.DebugLogger.LogWindowInfo(_targetHwnd, "RIGHT-CLICK on pin button");
        };

        Content = _button;

        SourceInitialized += HandleSourceInitialized;
        Loaded += (_, _) => ApplySettings(_settings);
    }

    public event EventHandler? PinRequested;

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings.Clone();
        ApplySizing();
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
        var (scaleX, scaleY) = GetDpiScale();
        var buttonWidthPixels = _settings.ButtonWidth;
        var buttonHeightPixels = _settings.ButtonHeight;

        var windowWidth = bounds.Right - bounds.Left;

        int leftPixels;
        int topPixels;

        if (_settings.CenterButton)
        {
            leftPixels = bounds.Left + (windowWidth - buttonWidthPixels) / 2 + _settings.ButtonOffsetX;
        }
        else
        {
            leftPixels = bounds.Right - _settings.ButtonOffsetX - buttonWidthPixels;
        }

        topPixels = bounds.Top + _settings.ButtonOffsetY;

        var leftDip = leftPixels / scaleX;
        var topDip = topPixels / scaleY;
        var widthDip = buttonWidthPixels / scaleX;
        var heightDip = buttonHeightPixels / scaleY;

        Width = widthDip;
        Height = heightDip;
        Left = leftDip;
        Top = topDip;

        if (_windowHandle != IntPtr.Zero)
        {
            // Position overlay just above the target window in z-order
            // Get the window in front of the target, then position after it
            var windowInFront = NativeMethods.GetWindow(_targetHwnd, NativeMethods.GW_HWNDPREV);
            var insertAfter = windowInFront != IntPtr.Zero ? windowInFront : _targetHwnd;

            var flags = NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE
                        | NativeMethods.SetWindowPosFlags.SWP_NOOWNERZORDER
                        | NativeMethods.SetWindowPosFlags.SWP_NOSENDCHANGING;

            NativeMethods.SetWindowPos(
                _windowHandle,
                insertAfter,
                leftPixels,
                topPixels,
                buttonWidthPixels,
                buttonHeightPixels,
                flags);
        }
    }

    private void ApplySizing()
    {
        var (scaleX, scaleY) = GetDpiScale();
        var widthDip = _settings.ButtonWidth / scaleX;
        var heightDip = _settings.ButtonHeight / scaleY;

        _button.Width = widthDip;
        _button.Height = heightDip;
        Width = widthDip;
        Height = heightDip;
    }

    private void LoadImages()
    {
        _defaultImage = LoadImage(_settings.ButtonImagePath) ?? CreateFallbackImage(System.Windows.Media.Colors.White, System.Windows.Media.Colors.Transparent);
        _hoverImage = LoadImage(_settings.ButtonHoverImagePath) ?? _defaultImage;
        _pinnedImage = LoadImage(_settings.ButtonPinnedImagePath);

        if (_pinnedImage is null)
        {
            var highlightColor = ParseColor(_settings.PinnedHighlightColor) ?? System.Windows.Media.Colors.LightSkyBlue;
            _pinnedImage = CreateFallbackImage(highlightColor, System.Windows.Media.Color.FromArgb(40, 0, 0, 0));
        }
    }

    private static System.Windows.Media.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateImageSource()
    {
        if (_isPinned)
        {
            _image.Source = _pinnedImage ?? _defaultImage;
        }
        else if (_isMouseOver)
        {
            _image.Source = _hoverImage ?? _defaultImage;
        }
        else
        {
            _image.Source = _defaultImage;
        }
    }

    private void HandleSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            var handle = source.Handle;
            _windowHandle = handle;
            var styles = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            // Use TOOLWINDOW and NOACTIVATE, but NOT TOPMOST (we manage z-order manually)
            styles |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, styles);
            source.AddHook(WndProc);
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

    private static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource CreateFallbackImage(System.Windows.Media.Color strokeColor, System.Windows.Media.Color fillColor)
    {
        var geometry = Geometry.Parse("M12,2 L8,6 L9,7 L7,9 L9,11 L11,9 L12,10 L16,6 z");

        var strokeBrush = new SolidColorBrush(strokeColor);
        strokeBrush.Freeze();
        var fillBrush = new SolidColorBrush(fillColor);
        fillBrush.Freeze();

        var drawingGroup = new DrawingGroup();
        drawingGroup.Children.Add(new GeometryDrawing(fillBrush, new System.Windows.Media.Pen(strokeBrush, 1.4), geometry));
        drawingGroup.Freeze();

        return new DrawingImage(drawingGroup);
    }

    public void EnsureVisibleOnTop()
    {
        if (!IsVisible)
        {
            Show();
        }
        else
        {
            Visibility = Visibility.Visible;
        }

        // Position just above target window in z-order
        if (_windowHandle != IntPtr.Zero && _targetHwnd != IntPtr.Zero)
        {
            var windowInFront = NativeMethods.GetWindow(_targetHwnd, NativeMethods.GW_HWNDPREV);
            var insertAfter = windowInFront != IntPtr.Zero ? windowInFront : _targetHwnd;

            NativeMethods.SetWindowPos(
                _windowHandle,
                insertAfter,
                0, 0, 0, 0,
                NativeMethods.SetWindowPosFlags.SWP_NOMOVE
                | NativeMethods.SetWindowPosFlags.SWP_NOSIZE
                | NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE
                | NativeMethods.SetWindowPosFlags.SWP_NOOWNERZORDER);
        }
    }

    private (double ScaleX, double ScaleY) GetDpiScale()
    {
        if (_targetHwnd != IntPtr.Zero)
        {
            try
            {
                var dpi = NativeMethods.GetDpiForWindow(_targetHwnd);
                if (dpi > 0)
                {
                    var scale = dpi / 96.0;
                    return (scale, scale);
                }
            }
            catch
            {
                // fall back to visual DPI
            }
        }

        var visualDpi = VisualTreeHelper.GetDpi(this);
        return (visualDpi.DpiScaleX, visualDpi.DpiScaleY);
    }
}
