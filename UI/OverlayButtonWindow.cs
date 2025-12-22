using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using WindowPinTray.Interop;
using WindowPinTray.Models;

namespace WindowPinTray.UI;

internal sealed class OverlayButtonWindow : Window
{
    private readonly IntPtr _targetHwnd;
    private readonly System.Windows.Controls.Button _button;
    private readonly System.Windows.Controls.Image _image;
    private IntPtr _windowHandle;
    private bool _ownerSet;

    private ImageSource? _defaultImage;
    private ImageSource? _hoverImage;
    private ImageSource? _pinnedImage;
    private ImageSource? _pinnedHoverImage;

    private AppSettings _settings;
    private bool _isPinned;
    private bool _isMouseOver;
    private NativeMethods.RECT _lastBounds;

    // Track last applied position to avoid redundant SetWindowPos calls
    private int _lastLeftPixels;
    private int _lastTopPixels;
    private int _lastWidthPixels;
    private int _lastHeightPixels;
    private DateTime _lastZOrderUpdate = DateTime.MinValue;
    private static readonly TimeSpan ZOrderThrottle = TimeSpan.FromMilliseconds(200);
    private double _lastScaleX = 1.0;
    private double _lastScaleY = 1.0;

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
        ShowActivated = false;

        _image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Fill,
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
        _button.MouseRightButtonDown += (_, e) =>
        {
            Services.DebugLogger.LogWindowInfo(_targetHwnd, "RIGHT-CLICK on pin button");
            e.Handled = true;
        };
        _button.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
        };

        Content = _button;

        SourceInitialized += HandleSourceInitialized;
        Loaded += (_, _) => ApplySettings(_settings);
    }

    public event EventHandler? PinRequested;

    public bool IsOwnerSet => _ownerSet;
    public IntPtr Handle => _windowHandle;

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings.Clone();
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

    /// <summary>
    /// Forces z-order update regardless of throttle. Call when target window gains focus.
    /// </summary>
    public void ForceZOrderUpdate()
    {
        if (_windowHandle == IntPtr.Zero)
            return;

        bool targetIsTopMost = NativeMethods.IsWindowTopMost(_targetHwnd);

        // If we're properly owned and target is not topmost, let Windows handle z-order.
        // The owner relationship automatically keeps us above our owner but below other windows.
        // Only force z-order when: target is topmost (we need topmost too) or we're not owned.
        if (_ownerSet && !targetIsTopMost)
        {
            _lastZOrderUpdate = DateTime.UtcNow;
            return; // Let Windows manage z-order via owner relationship
        }

        var insertAfter = NativeMethods.HWND_TOPMOST;

        var flags = NativeMethods.SetWindowPosFlags.SWP_NOMOVE
                    | NativeMethods.SetWindowPosFlags.SWP_NOSIZE
                    | NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE
                    | NativeMethods.SetWindowPosFlags.SWP_NOOWNERZORDER
                    | NativeMethods.SetWindowPosFlags.SWP_NOSENDCHANGING
                    | NativeMethods.SetWindowPosFlags.SWP_NOREDRAW
                    | NativeMethods.SetWindowPosFlags.SWP_DEFERERASE;

        NativeMethods.SetWindowPos(_windowHandle, insertAfter, 0, 0, 0, 0, flags);
        _lastZOrderUpdate = DateTime.UtcNow;
    }

    private void ApplyPositionAndSize()
    {
        if (_windowHandle == IntPtr.Zero) return;

        var (scaleX, scaleY) = GetDpiScale();

        var windowWidth = _lastBounds.Right - _lastBounds.Left;
        var windowHeight = _lastBounds.Bottom - _lastBounds.Top;

        int buttonWidthPixels;
        int buttonHeightPixels;

        if (windowWidth > 0 && windowHeight > 0)
        {
            buttonWidthPixels = Math.Min(_settings.ButtonWidth, windowWidth);
            buttonHeightPixels = Math.Min(_settings.ButtonHeight, windowHeight);
        }
        else
        {
            buttonWidthPixels = _settings.ButtonWidth;
            buttonHeightPixels = _settings.ButtonHeight;
        }

        var widthDip = buttonWidthPixels / scaleX;
        var heightDip = buttonHeightPixels / scaleY;

        var dpiOrSizeChanged =
            buttonWidthPixels != _lastWidthPixels
            || buttonHeightPixels != _lastHeightPixels
            || !AreClose(scaleX, _lastScaleX)
            || !AreClose(scaleY, _lastScaleY);

        if (dpiOrSizeChanged)
        {
            _button.Width = widthDip;
            _button.Height = heightDip;
            Width = widthDip;
            Height = heightDip;

            _lastWidthPixels = buttonWidthPixels;
            _lastHeightPixels = buttonHeightPixels;
            _lastScaleX = scaleX;
            _lastScaleY = scaleY;
        }

        if (windowWidth <= 0 || windowHeight <= 0)
            return;

        int leftPixels;
        int topPixels;

        if (_settings.CenterButton)
        {
            leftPixels = _lastBounds.Left + (windowWidth - buttonWidthPixels) / 2 + _settings.ButtonOffsetX;
        }
        else
        {
            leftPixels = _lastBounds.Right - _settings.ButtonOffsetX - buttonWidthPixels;
        }

        topPixels = _lastBounds.Top + _settings.ButtonOffsetY;

        // Strictly use SetWindowPos. Do not touch WPF Left/Top as it triggers redundant Win32 moves
        // and potentially interferes with focus/activation.
        
        var positionChanged = leftPixels != _lastLeftPixels || topPixels != _lastTopPixels;
        var now = DateTime.UtcNow;
        var zOrderDue = now - _lastZOrderUpdate >= ZOrderThrottle;

        if (positionChanged || zOrderDue)
        {
            _lastLeftPixels = leftPixels;
            _lastTopPixels = topPixels;

            var flags = NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE |
                        NativeMethods.SetWindowPosFlags.SWP_NOOWNERZORDER |
                        NativeMethods.SetWindowPosFlags.SWP_NOSENDCHANGING |
                        NativeMethods.SetWindowPosFlags.SWP_NOREDRAW |
                        NativeMethods.SetWindowPosFlags.SWP_DEFERERASE;
            
            IntPtr insertAfter = NativeMethods.HWND_TOP; 
            bool needZUpdate = false;

            if (zOrderDue)
            {
                bool targetIsTopMost = NativeMethods.IsWindowTopMost(_targetHwnd);
                bool selfIsTopMost = NativeMethods.IsWindowTopMost(_windowHandle);

                if (_ownerSet && !targetIsTopMost)
                {
                    // Owned and target is not topmost - let Windows handle z-order.
                    // Only intervene if we're incorrectly topmost.
                    if (selfIsTopMost)
                    {
                        needZUpdate = true;
                        insertAfter = NativeMethods.HWND_NOTOPMOST;
                    }
                    // Otherwise, don't touch z-order at all - owner relationship handles it
                }
                else if (targetIsTopMost || !_ownerSet)
                {
                    // Target is topmost or we're not owned - we need to be topmost
                    if (!selfIsTopMost)
                    {
                        needZUpdate = true;
                        insertAfter = NativeMethods.HWND_TOPMOST;
                    }
                }
            }

            if (needZUpdate)
            {
                _lastZOrderUpdate = now;
            }
            else
            {
                flags |= NativeMethods.SetWindowPosFlags.SWP_NOZORDER;
            }

            if (!positionChanged)
            {
                flags |= NativeMethods.SetWindowPosFlags.SWP_NOMOVE | NativeMethods.SetWindowPosFlags.SWP_NOSIZE;
            }

            NativeMethods.SetWindowPos(_windowHandle, insertAfter, leftPixels, topPixels, buttonWidthPixels, buttonHeightPixels, flags);
        }
    }

    public NativeMethods.RECT GetButtonRect()
    {
        return new NativeMethods.RECT
        {
            Left = _lastLeftPixels,
            Top = _lastTopPixels,
            Right = _lastLeftPixels + _lastWidthPixels,
            Bottom = _lastTopPixels + _lastHeightPixels
        };
    }

    private void LoadImages()
    {
        _defaultImage = LoadImage(_settings.ButtonImagePath) ?? CreateFallbackImage(System.Windows.Media.Colors.White, System.Windows.Media.Colors.Transparent);
        _hoverImage = LoadImage(_settings.ButtonHoverImagePath) ?? _defaultImage;
        _pinnedImage = LoadImage(_settings.ButtonPinnedImagePath);
        _pinnedHoverImage = LoadImage(_settings.ButtonPinnedHoverImagePath);

        var highlightColor = ParseColor(_settings.PinnedHighlightColor) ?? System.Windows.Media.Colors.LightSkyBlue;
        if (_pinnedImage is null)
        {
            _pinnedImage = CreateFallbackImage(highlightColor, System.Windows.Media.Color.FromArgb(40, 0, 0, 0));
        }

        if (_pinnedHoverImage is null)
        {
            // Use a slightly brighter/saturated version for hover
            _pinnedHoverImage = _pinnedImage;
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
        if (_isPinned && _isMouseOver)
        {
            _image.Source = _pinnedHoverImage ?? _pinnedImage ?? _defaultImage;
        }
        else if (_isPinned)
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

            TrySetOwnerWindow();

            var styles = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            styles |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

            // If owner couldn't be set (e.g., elevated windows), use TOPMOST to ensure visibility.
            // UIPI prevents non-elevated windows from being positioned above elevated windows
            // via normal z-order, but TOPMOST works across elevation boundaries.
            if (!_ownerSet)
            {
                styles |= NativeMethods.WS_EX_TOPMOST;
            }

            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, styles);
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

    private static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".svg")
            {
                return LoadSvgImage(path);
            }

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

    private static ImageSource? LoadSvgImage(string path)
    {
        try
        {
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = true,
                TextAsGeometry = false
            };

            var converter = new FileSvgReader(settings);
            var drawing = converter.Read(path);
            if (drawing != null)
            {
                var drawingImage = new DrawingImage(drawing);
                drawingImage.Freeze();
                return drawingImage;
            }
        }
        catch
        {
            // Fall through to return null
        }

        return null;
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
        else if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }

        // If the target window is foreground, we want to be very responsive with Z-order.
        // ApplyPositionAndSize handles periodic updates, but we can trigger one here
        // if we're the active window's overlay and it's been a bit since the last update.
        if (DateTime.UtcNow - _lastZOrderUpdate > ZOrderThrottle)
        {
            ForceZOrderUpdate();
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

    private void TrySetOwnerWindow()
    {
        if (_ownerSet)
        {
            return;
        }

        try
        {
            if (_targetHwnd != IntPtr.Zero && NativeMethods.IsWindow(_targetHwnd))
            {
                var helper = new WindowInteropHelper(this)
                {
                    Owner = _targetHwnd
                };

                // Verify the owner was actually set at the Win32 level.
                // WPF's helper.Owner just returns what we set, but the actual
                // Win32 relationship may not have been established (e.g., for
                // UWP apps, elevated windows, or cross-process scenarios).
                if (_windowHandle != IntPtr.Zero)
                {
                    var actualOwner = NativeMethods.GetWindow(_windowHandle, NativeMethods.GW_OWNER);
                    _ownerSet = actualOwner == _targetHwnd;
                }
                else
                {
                    _ownerSet = false;
                }
            }
        }
        catch
        {
            _ownerSet = false;
        }
    }

    private static bool AreClose(double a, double b)
    {
        return Math.Abs(a - b) < 0.0001;
    }
}
