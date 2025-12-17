using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using TextBox = System.Windows.Controls.TextBox;

namespace WindowPinTray.UI;

public partial class ColorPickerWindow : Window
{
    private bool _updating;
    public string SelectedColor { get; private set; } = "#87CEFA";

    public ColorPickerWindow(string initialColor)
    {
        InitializeComponent();
        SelectedColor = initialColor;
        SetColorFromHex(initialColor);
    }

    private void SetColorFromHex(string hex)
    {
        _updating = true;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            HexTextBox.Text = hex.ToUpperInvariant();

            RSlider.Value = color.R;
            GSlider.Value = color.G;
            BSlider.Value = color.B;
            RTextBox.Text = color.R.ToString();
            GTextBox.Text = color.G.ToString();
            BTextBox.Text = color.B.ToString();

            var (h, s, b) = RgbToHsb(color.R, color.G, color.B);
            HSlider.Value = h;
            SSlider.Value = s;
            BrSlider.Value = b;
            HTextBox.Text = ((int)h).ToString();
            STextBox.Text = ((int)s).ToString();
            BrTextBox.Text = ((int)b).ToString();

            ColorPreview.Background = new SolidColorBrush(color);
        }
        catch
        {
            ColorPreview.Background = new SolidColorBrush(Colors.LightSkyBlue);
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateFromRgb()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var r = (byte)Math.Clamp((int)RSlider.Value, 0, 255);
            var g = (byte)Math.Clamp((int)GSlider.Value, 0, 255);
            var b = (byte)Math.Clamp((int)BSlider.Value, 0, 255);

            var color = Color.FromRgb(r, g, b);
            var hex = $"#{r:X2}{g:X2}{b:X2}";

            HexTextBox.Text = hex;
            SelectedColor = hex;
            ColorPreview.Background = new SolidColorBrush(color);

            var (h, s, br) = RgbToHsb(r, g, b);
            HSlider.Value = h;
            SSlider.Value = s;
            BrSlider.Value = br;
            HTextBox.Text = ((int)h).ToString();
            STextBox.Text = ((int)s).ToString();
            BrTextBox.Text = ((int)br).ToString();
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateFromHsb()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var h = Math.Clamp(HSlider.Value, 0, 360);
            var s = Math.Clamp(SSlider.Value, 0, 100);
            var b = Math.Clamp(BrSlider.Value, 0, 100);

            var (r, g, bl) = HsbToRgb(h, s, b);
            var color = Color.FromRgb(r, g, bl);
            var hex = $"#{r:X2}{g:X2}{bl:X2}";

            HexTextBox.Text = hex;
            SelectedColor = hex;
            ColorPreview.Background = new SolidColorBrush(color);

            RSlider.Value = r;
            GSlider.Value = g;
            BSlider.Value = bl;
            RTextBox.Text = r.ToString();
            GTextBox.Text = g.ToString();
            BTextBox.Text = bl.ToString();
        }
        finally
        {
            _updating = false;
        }
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsLoaded) return;
        RTextBox.Text = ((int)RSlider.Value).ToString();
        GTextBox.Text = ((int)GSlider.Value).ToString();
        BTextBox.Text = ((int)BSlider.Value).ToString();
        UpdateFromRgb();
    }

    private void RgbTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !IsLoaded) return;
        if (sender is not TextBox tb) return;
        if (!int.TryParse(tb.Text, out var val)) return;
        val = Math.Clamp(val, 0, 255);

        _updating = true;
        switch (tb.Tag?.ToString())
        {
            case "R": RSlider.Value = val; break;
            case "G": GSlider.Value = val; break;
            case "B": BSlider.Value = val; break;
        }
        _updating = false;
        UpdateFromRgb();
    }

    private void HsbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsLoaded) return;
        HTextBox.Text = ((int)HSlider.Value).ToString();
        STextBox.Text = ((int)SSlider.Value).ToString();
        BrTextBox.Text = ((int)BrSlider.Value).ToString();
        UpdateFromHsb();
    }

    private void HsbTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !IsLoaded) return;
        if (sender is not TextBox tb) return;
        if (!int.TryParse(tb.Text, out var val)) return;

        _updating = true;
        switch (tb.Tag?.ToString())
        {
            case "H": HSlider.Value = Math.Clamp(val, 0, 360); break;
            case "S": SSlider.Value = Math.Clamp(val, 0, 100); break;
            case "Br": BrSlider.Value = Math.Clamp(val, 0, 100); break;
        }
        _updating = false;
        UpdateFromHsb();
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !IsLoaded) return;
        var hex = HexTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(hex)) return;
        if (!hex.StartsWith("#")) hex = "#" + hex;
        if (hex.Length != 7) return;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            _updating = true;

            RSlider.Value = color.R;
            GSlider.Value = color.G;
            BSlider.Value = color.B;
            RTextBox.Text = color.R.ToString();
            GTextBox.Text = color.G.ToString();
            BTextBox.Text = color.B.ToString();

            var (h, s, b) = RgbToHsb(color.R, color.G, color.B);
            HSlider.Value = h;
            SSlider.Value = s;
            BrSlider.Value = b;
            HTextBox.Text = ((int)h).ToString();
            STextBox.Text = ((int)s).ToString();
            BrTextBox.Text = ((int)b).ToString();

            SelectedColor = hex.ToUpperInvariant();
            ColorPreview.Background = new SolidColorBrush(color);
            _updating = false;
        }
        catch { }
    }

    private static (double H, double S, double B) RgbToHsb(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rd) h = 60 * (((gd - bd) / delta) % 6);
            else if (max == gd) h = 60 * (((bd - rd) / delta) + 2);
            else h = 60 * (((rd - gd) / delta) + 4);
        }
        if (h < 0) h += 360;

        var s = max == 0 ? 0 : (delta / max) * 100;
        var br = max * 100;

        return (h, s, br);
    }

    private static (byte R, byte G, byte B) HsbToRgb(double h, double s, double b)
    {
        s /= 100;
        b /= 100;

        var c = b * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = b - c;

        double r = 0, g = 0, bl = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; bl = x; }
        else if (h < 240) { g = x; bl = c; }
        else if (h < 300) { r = x; bl = c; }
        else { r = c; bl = x; }

        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((bl + m) * 255));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
