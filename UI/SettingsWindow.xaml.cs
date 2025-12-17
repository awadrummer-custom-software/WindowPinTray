using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowPinTray.Models;
using WindowPinTray.Services;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace WindowPinTray.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private bool _isUpdating;

    public SettingsWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();

        Loaded += (_, _) => RefreshInputs();
        Closing += HandleClosing;
        HighlightColorTextBox.TextChanged += HandleColorTextChanged;
    }

    public void ShowFromTray()
    {
        RefreshInputs();
        Show();
        Activate();
        Focus();
    }

    private void RefreshInputs()
    {
        _isUpdating = true;
        try
        {
            var settings = _settingsService.CurrentSettings;
            StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;

            WidthSlider.Value = settings.ButtonWidth;
            HeightSlider.Value = settings.ButtonHeight;
            ButtonWidthTextBox.Text = settings.ButtonWidth.ToString();
            ButtonHeightTextBox.Text = settings.ButtonHeight.ToString();

            OffsetXSlider.Value = settings.ButtonOffsetX;
            OffsetYSlider.Value = settings.ButtonOffsetY;
            OffsetXTextBox.Text = settings.ButtonOffsetX.ToString();
            OffsetYTextBox.Text = settings.ButtonOffsetY.ToString();

            CenterButtonCheckBox.IsChecked = settings.CenterButton;

            HighlightColorTextBox.Text = settings.PinnedHighlightColor;
            UpdateColorPreview(settings.PinnedHighlightColor);

            ButtonImageTextBox.Text = settings.ButtonImagePath ?? string.Empty;
            HoverImageTextBox.Text = settings.ButtonHoverImagePath ?? string.Empty;

            IgnoredTitlesTextBox.Text = string.Join(Environment.NewLine, settings.IgnoredWindowTitles);
            IgnoredExecutablesTextBox.Text = string.Join(Environment.NewLine, settings.IgnoredProcessPaths);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void HandleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdating || !IsLoaded) return;

        _isUpdating = true;
        try
        {
            if (sender == WidthSlider && ButtonWidthTextBox != null)
                ButtonWidthTextBox.Text = ((int)WidthSlider.Value).ToString();
            else if (sender == HeightSlider && ButtonHeightTextBox != null)
                ButtonHeightTextBox.Text = ((int)HeightSlider.Value).ToString();
            else if (sender == OffsetXSlider && OffsetXTextBox != null)
                OffsetXTextBox.Text = ((int)OffsetXSlider.Value).ToString();
            else if (sender == OffsetYSlider && OffsetYTextBox != null)
                OffsetYTextBox.Text = ((int)OffsetYSlider.Value).ToString();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void HandleSizeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (sender is not TextBox textBox) return;

        _isUpdating = true;
        try
        {
            if (int.TryParse(textBox.Text, out var value))
            {
                switch (textBox.Tag?.ToString())
                {
                    case "Width":
                        WidthSlider.Value = Math.Clamp(value, 1, 256);
                        break;
                    case "Height":
                        HeightSlider.Value = Math.Clamp(value, 1, 256);
                        break;
                    case "OffsetX":
                        OffsetXSlider.Value = Math.Clamp(value, 0, 400);
                        break;
                    case "OffsetY":
                        OffsetYSlider.Value = Math.Clamp(value, -200, 400);
                        break;
                }
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void HandleColorTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateColorPreview(HighlightColorTextBox.Text);
    }

    private void UpdateColorPreview(string? colorText)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(colorText))
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorText);
                ColorPreview.Background = new SolidColorBrush(color);
                return;
            }
        }
        catch { }

        ColorPreview.Background = new SolidColorBrush(Colors.LightSkyBlue);
    }

    private AppSettings BuildSettingsFromInputs()
    {
        var updated = _settingsService.CurrentSettings.Clone();
        updated.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        updated.ButtonWidth = (int)WidthSlider.Value;
        updated.ButtonHeight = (int)HeightSlider.Value;
        updated.ButtonOffsetX = (int)OffsetXSlider.Value;
        updated.ButtonOffsetY = (int)OffsetYSlider.Value;
        updated.CenterButton = CenterButtonCheckBox.IsChecked == true;
        updated.PinnedHighlightColor = NormalizePath(HighlightColorTextBox.Text) ?? "#87CEFA";
        updated.ButtonImagePath = NormalizePath(ButtonImageTextBox.Text);
        updated.ButtonHoverImagePath = NormalizePath(HoverImageTextBox.Text);
        updated.IgnoredWindowTitles = ParseLines(IgnoredTitlesTextBox);
        updated.IgnoredProcessPaths = ParseLines(IgnoredExecutablesTextBox);
        return updated;
    }

    private void HandleApplyClick(object sender, RoutedEventArgs e)
    {
        var updated = BuildSettingsFromInputs();
        _settingsService.Update(updated);
    }

    private void HandleSaveClick(object sender, RoutedEventArgs e)
    {
        var updated = BuildSettingsFromInputs();
        _settingsService.Update(updated);
        Hide();
    }

    private void HandleCancelClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void HandleBrowseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|All files|*.*",
            CheckFileExists = true
        };

        if (button.Tag is string tag)
        {
            dialog.Title = tag switch
            {
                "Hover" => "Select mouse-over image",
                _ => "Select button image"
            };

            var existingPath = tag switch
            {
                "Hover" => HoverImageTextBox.Text,
                _ => ButtonImageTextBox.Text
            };

            if (!string.IsNullOrWhiteSpace(existingPath))
            {
                dialog.InitialDirectory = SafeGetDirectory(existingPath);
                dialog.FileName = Path.GetFileName(existingPath);
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            switch (button.Tag)
            {
                case "Hover":
                    HoverImageTextBox.Text = dialog.FileName;
                    break;
                default:
                    ButtonImageTextBox.Text = dialog.FileName;
                    break;
            }
        }
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void HandleCenterToggleChanged(object sender, RoutedEventArgs e)
    {
    }

    private static string? NormalizePath(string? input)
    {
        var trimmed = input?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? SafeGetDirectory(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                return directory;
        }
        catch { }
        return null;
    }

    private static List<string> ParseLines(TextBox textBox)
    {
        return textBox.Text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToList();
    }
}
