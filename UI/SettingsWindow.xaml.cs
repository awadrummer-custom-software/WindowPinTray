using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WindowPinTray.Models;
using WindowPinTray.Services;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace WindowPinTray.UI;

public partial class SettingsWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly SettingsService _settingsService;
    private bool _isLoading;

    public SettingsWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();

        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += (_, _) =>
        {
            RestoreWindowPosition();
            RefreshInputs();
        };
        Closing += HandleClosing;
    }

    private void EnableDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    public void ShowFromTray()
    {
        RestoreWindowPosition();
        RefreshInputs();
        Show();
        Activate();
        Focus();
    }

    private void RestoreWindowPosition()
    {
        var settings = _settingsService.CurrentSettings;
        if (settings.SettingsWindowWidth.HasValue && settings.SettingsWindowHeight.HasValue)
        {
            Width = settings.SettingsWindowWidth.Value;
            Height = settings.SettingsWindowHeight.Value;
        }

        if (settings.SettingsWindowLeft.HasValue && settings.SettingsWindowTop.HasValue)
        {
            Left = settings.SettingsWindowLeft.Value;
            Top = settings.SettingsWindowTop.Value;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    private void SaveWindowPosition()
    {
        var updated = _settingsService.CurrentSettings.Clone();
        updated.SettingsWindowLeft = Left;
        updated.SettingsWindowTop = Top;
        updated.SettingsWindowWidth = Width;
        updated.SettingsWindowHeight = Height;
        _settingsService.Update(updated);
    }

    private void RefreshInputs()
    {
        _isLoading = true;
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

            HighlightColorTextBox.Text = settings.PinnedHighlightColor ?? string.Empty;
            UpdateColorPreview(settings.PinnedHighlightColor);

            ButtonImageTextBox.Text = settings.ButtonImagePath ?? string.Empty;
            HoverImageTextBox.Text = settings.ButtonHoverImagePath ?? string.Empty;
            PinnedImageTextBox.Text = settings.ButtonPinnedImagePath ?? string.Empty;
            PinnedHoverImageTextBox.Text = settings.ButtonPinnedHoverImagePath ?? string.Empty;

            IgnoredTitlesTextBox.Text = string.Join(Environment.NewLine, settings.IgnoredWindowTitles);
            IgnoredExecutablesTextBox.Text = string.Join(Environment.NewLine, settings.IgnoredProcessPaths);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ApplySettings()
    {
        if (_isLoading) return;

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
        updated.ButtonPinnedImagePath = NormalizePath(PinnedImageTextBox.Text);
        updated.ButtonPinnedHoverImagePath = NormalizePath(PinnedHoverImageTextBox.Text);
        updated.IgnoredWindowTitles = ParseLines(IgnoredTitlesTextBox);
        updated.IgnoredProcessPaths = ParseLines(IgnoredExecutablesTextBox);

        _settingsService.Update(updated);
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;

        _isLoading = true;
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
            _isLoading = false;
        }

        ApplySettings();
    }

    private void OnSizeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        if (sender is not TextBox textBox) return;

        _isLoading = true;
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
            _isLoading = false;
        }

        ApplySettings();
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        ApplySettings();
    }

    private void OnIgnoreListChanged(object sender, TextChangedEventArgs e)
    {
        ApplySettings();
    }

    private void ColorPreview_Click(object sender, MouseButtonEventArgs e)
    {
        var currentColor = HighlightColorTextBox.Text;
        if (string.IsNullOrWhiteSpace(currentColor)) currentColor = "#87CEFA";

        var picker = new ColorPickerWindow(currentColor) { Owner = this };
        if (picker.ShowDialog() == true)
        {
            HighlightColorTextBox.Text = picker.SelectedColor;
            UpdateColorPreview(picker.SelectedColor);
            ApplySettings();
        }
    }

    private void ClearColor_Click(object sender, RoutedEventArgs e)
    {
        HighlightColorTextBox.Text = string.Empty;
        UpdateColorPreview(null);
        ApplySettings();
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        switch (button.Tag?.ToString())
        {
            case "ClearDefault":
                ButtonImageTextBox.Text = string.Empty;
                break;
            case "ClearHover":
                HoverImageTextBox.Text = string.Empty;
                break;
            case "ClearPinned":
                PinnedImageTextBox.Text = string.Empty;
                break;
            case "ClearPinnedHover":
                PinnedHoverImageTextBox.Text = string.Empty;
                break;
        }

        ApplySettings();
    }

    private void UpdateColorPreview(string? colorText)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(colorText))
            {
                var color = (Color)ColorConverter.ConvertFromString(colorText);
                ColorPreview.Background = new SolidColorBrush(color);
                return;
            }
        }
        catch { }

        ColorPreview.Background = new SolidColorBrush(Colors.Transparent);
    }

    private void HandleBrowseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.svg|All files|*.*",
            CheckFileExists = true
        };

        var tag = button.Tag?.ToString();
        dialog.Title = tag switch
        {
            "Hover" => "Select hover image",
            "Pinned" => "Select pinned image",
            "PinnedHover" => "Select pinned hover image",
            _ => "Select default image"
        };

        var existingPath = tag switch
        {
            "Hover" => HoverImageTextBox.Text,
            "Pinned" => PinnedImageTextBox.Text,
            "PinnedHover" => PinnedHoverImageTextBox.Text,
            _ => ButtonImageTextBox.Text
        };

        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            dialog.InitialDirectory = SafeGetDirectory(existingPath);
            dialog.FileName = Path.GetFileName(existingPath);
        }

        if (dialog.ShowDialog(this) == true)
        {
            switch (tag)
            {
                case "Hover":
                    HoverImageTextBox.Text = dialog.FileName;
                    break;
                case "Pinned":
                    PinnedImageTextBox.Text = dialog.FileName;
                    break;
                case "PinnedHover":
                    PinnedHoverImageTextBox.Text = dialog.FileName;
                    break;
                default:
                    ButtonImageTextBox.Text = dialog.FileName;
                    break;
            }

            ApplySettings();
        }
    }

    private void HandleCloseClick(object sender, RoutedEventArgs e)
    {
        SaveWindowPosition();
        Hide();
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        SaveWindowPosition();
        Hide();
    }

    private void HandleExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON files|*.json|All files|*.*",
            DefaultExt = ".json",
            FileName = "WindowPinTray-settings.json",
            Title = "Export Settings"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var settings = _settingsService.CurrentSettings;
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dialog.FileName, json);
                MessageBox.Show(this, "Settings exported successfully.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export settings: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void HandleImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files|*.json|All files|*.*",
            Title = "Import Settings"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var imported = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (imported != null)
                {
                    _settingsService.Update(imported);
                    RefreshInputs();
                    MessageBox.Show(this, "Settings imported successfully.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(this, "Invalid settings file.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to import settings: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
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
