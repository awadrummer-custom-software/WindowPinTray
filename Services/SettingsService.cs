using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WindowPinTray.Models;

namespace WindowPinTray.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;
    private readonly StartupRegistrationService _startupRegistrationService;
    private AppSettings _currentSettings;

    public SettingsService()
    {
        _startupRegistrationService = new StartupRegistrationService();
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowPinTray",
            "settings.json");

        _currentSettings = LoadSettings();
        _currentSettings.StartWithWindows = _startupRegistrationService.IsEnabled();
    }

    public AppSettings CurrentSettings => _currentSettings.Clone();

    public event EventHandler<AppSettings>? SettingsChanged;

    public void Update(AppSettings updated)
    {
        var sanitized = Sanitize(updated);
        _startupRegistrationService.SetEnabled(sanitized.StartWithWindows);
        sanitized.StartWithWindows = _startupRegistrationService.IsEnabled();
        _currentSettings = sanitized;
        SaveSettings(_currentSettings);
        SettingsChanged?.Invoke(this, _currentSettings.Clone());
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded is not null)
                {
                    return Sanitize(loaded);
                }
            }
        }
        catch
        {
            // ignore and fall back to defaults
        }

        return AppSettings.CreateDefault();
    }

    private void SaveSettings(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // ignore write errors; user can retry saving later.
        }
    }

    private static AppSettings Sanitize(AppSettings settings)
    {
        var clone = settings.Clone();
        clone.ButtonSize = Math.Clamp(clone.ButtonSize, 1, 256);
        clone.ButtonWidth = Math.Clamp(clone.ButtonWidth, 1, 256);
        clone.ButtonHeight = Math.Clamp(clone.ButtonHeight, 1, 256);
        clone.ButtonOffsetX = Math.Clamp(clone.ButtonOffsetX, 0, 400);
        clone.ButtonOffsetY = Math.Clamp(clone.ButtonOffsetY, -200, 400);
        clone.ButtonImagePath = NormalizePath(clone.ButtonImagePath);
        clone.ButtonHoverImagePath = NormalizePath(clone.ButtonHoverImagePath);
        clone.ButtonPinnedImagePath = NormalizePath(clone.ButtonPinnedImagePath);
        clone.ButtonPinnedHoverImagePath = NormalizePath(clone.ButtonPinnedHoverImagePath);
        clone.PinnedHighlightColor = NormalizeColor(clone.PinnedHighlightColor);
        clone.CenterButton = clone.CenterButton;
        clone.IgnoredWindowTitles = NormalizeList(clone.IgnoredWindowTitles, isPath: false);
        clone.IgnoredProcessPaths = NormalizeList(clone.IgnoredProcessPaths, isPath: true);
        return clone;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "#87CEFA";
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("#") && (trimmed.Length == 7 || trimmed.Length == 9))
        {
            return trimmed.ToUpperInvariant();
        }

        return "#87CEFA";
    }

    private static List<string> NormalizeList(IEnumerable<string> source, bool isPath)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in source ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var value = item.Trim();

            if (isPath)
            {
                try
                {
                    value = Path.GetFullPath(value);
                }
                catch
                {
                    // fallback to trimmed value
                    value = item.Trim();
                }
            }

            if (seen.Add(value))
            {
                results.Add(value);
            }
        }

        return results;
    }
}
