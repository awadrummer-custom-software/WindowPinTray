using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowPinTray.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public int ButtonSize { get; set; } = 28;
    public int ButtonWidth { get; set; } = 28;
    public int ButtonHeight { get; set; } = 28;
    public int ButtonOffsetX { get; set; } = 12;
    public int ButtonOffsetY { get; set; } = 8;
    public string? ButtonImagePath { get; set; }
    public string? ButtonHoverImagePath { get; set; }
    public string? ButtonPinnedImagePath { get; set; }
    public string? ButtonPinnedHoverImagePath { get; set; }
    public string PinnedHighlightColor { get; set; } = "#87CEFA";
    public bool CenterButton { get; set; }
    public List<string> IgnoredWindowTitles { get; set; } = new();
    public List<string> IgnoredProcessPaths { get; set; } = new();

    // Window state
    public double? SettingsWindowLeft { get; set; }
    public double? SettingsWindowTop { get; set; }
    public double? SettingsWindowWidth { get; set; }
    public double? SettingsWindowHeight { get; set; }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            StartWithWindows = StartWithWindows,
            ButtonSize = ButtonSize,
            ButtonWidth = ButtonWidth,
            ButtonHeight = ButtonHeight,
            ButtonOffsetX = ButtonOffsetX,
            ButtonOffsetY = ButtonOffsetY,
            ButtonImagePath = ButtonImagePath,
            ButtonHoverImagePath = ButtonHoverImagePath,
            ButtonPinnedImagePath = ButtonPinnedImagePath,
            ButtonPinnedHoverImagePath = ButtonPinnedHoverImagePath,
            PinnedHighlightColor = PinnedHighlightColor,
            CenterButton = CenterButton,
            IgnoredWindowTitles = IgnoredWindowTitles.ToList(),
            IgnoredProcessPaths = IgnoredProcessPaths.ToList(),
            SettingsWindowLeft = SettingsWindowLeft,
            SettingsWindowTop = SettingsWindowTop,
            SettingsWindowWidth = SettingsWindowWidth,
            SettingsWindowHeight = SettingsWindowHeight
        };
    }

    public static AppSettings CreateDefault() => new();
}
