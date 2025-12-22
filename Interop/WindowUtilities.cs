using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WindowPinTray.Models;

namespace WindowPinTray.Interop;

internal static class WindowUtilities
{
    private static readonly uint CurrentProcessId = (uint)Process.GetCurrentProcess().Id;
    private static readonly ConcurrentDictionary<uint, string?> ProcessPathCache = new();

    internal static IReadOnlyList<IntPtr> EnumerateCandidateWindows(AppSettings settings)
    {
        var results = new List<IntPtr>();

        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (IsCandidateWindow(hwnd, settings))
                {
                    results.Add(hwnd);
                }

                return true;
            },
            IntPtr.Zero);

        return results;
    }

    internal static bool IsCandidateWindow(IntPtr hwnd, AppSettings settings)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        var owner = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER);
        if (owner != hwnd)
        {
            return false;
        }

        var style = (uint)NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CHILD) == NativeMethods.WS_CHILD)
        {
            return false;
        }

        if ((style & NativeMethods.WS_CAPTION) != NativeMethods.WS_CAPTION)
        {
            return false;
        }

        var exStyle = (uint)NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) == NativeMethods.WS_EX_TOOLWINDOW)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == CurrentProcessId)
        {
            return false;
        }

        var title = NativeMethods.GetWindowText(hwnd);
        if (IsIgnoredTitle(title, settings.IgnoredWindowTitles))
        {
            return false;
        }

        var processPath = GetProcessPath(processId);
        if (IsIgnoredProcess(processPath, settings.IgnoredProcessPaths))
        {
            return false;
        }

        return true;
    }

    internal static IntPtr NormalizeUpdateHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var rootOwner = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER);
        if (rootOwner != IntPtr.Zero)
        {
            return rootOwner;
        }

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        return root != IntPtr.Zero ? root : hwnd;
    }

    private static bool IsIgnoredTitle(string title, IList<string> ignoredTitles)
    {
        if (ignoredTitles.Count == 0 || string.IsNullOrEmpty(title))
        {
            return false;
        }

        foreach (var pattern in ignoredTitles)
        {
            if (title.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIgnoredProcess(string? processPath, IList<string> ignoredPaths)
    {
        if (ignoredPaths.Count == 0 || string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(processPath);

        foreach (var entry in ignoredPaths)
        {
            if (string.Equals(processPath, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(fileName)
                && string.Equals(fileName, Path.GetFileName(entry), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetProcessPath(uint processId)
    {
        if (ProcessPathCache.TryGetValue(processId, out var cached))
        {
            return string.IsNullOrEmpty(cached) ? null : cached;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName ?? string.Empty;
            ProcessPathCache[processId] = path;
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            ProcessPathCache[processId] = string.Empty;
            return null;
        }
    }

    internal static bool IsWindowExposed(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var targetRect))
        {
            return false;
        }

        var width = targetRect.Right - targetRect.Left;
        var height = targetRect.Bottom - targetRect.Top;

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // Sample a few points across the title bar area; if any point maps to the target window,
        // the window is at least partially exposed.
        var sampleYOffset = Math.Clamp(height / 10, 6, Math.Max(6, height - 2));
        var sampleY = Math.Clamp(targetRect.Top + sampleYOffset, targetRect.Top, targetRect.Bottom - 1);

        var inset = Math.Clamp(width / 6, 4, Math.Max(4, width / 3));
        var leftSample = Math.Clamp(targetRect.Left + inset, targetRect.Left, targetRect.Right - 1);
        var centerSample = Math.Clamp(targetRect.Left + width / 2, targetRect.Left, targetRect.Right - 1);
        var rightSample = Math.Clamp(targetRect.Right - inset, targetRect.Left, targetRect.Right - 1);

        var samplePoints = new[]
        {
            new NativeMethods.POINT(leftSample, sampleY),
            new NativeMethods.POINT(centerSample, sampleY),
            new NativeMethods.POINT(rightSample, sampleY)
        };

        foreach (var point in samplePoints)
        {
            var hit = NativeMethods.WindowFromPoint(point);
            if (hit == IntPtr.Zero)
            {
                continue;
            }

            var root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOTOWNER);
            if (root == IntPtr.Zero)
            {
                root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
            }

            if (root == IntPtr.Zero)
            {
                root = hit;
            }

            NativeMethods.GetWindowThreadProcessId(root, out var processId);
            if (processId == CurrentProcessId)
            {
                if (root == hwnd)
                {
                    return true;
                }

                continue;
            }

            if (root == hwnd)
            {
                return true;
            }
        }

        // Fallback: if every sampled point was obscured, treat the window as covered only if
        // another window overlaps nearly the entire area.
        var probe = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV);
        var targetArea = width * height;

        while (probe != IntPtr.Zero)
        {
            if (!NativeMethods.IsWindowVisible(probe) || NativeMethods.IsIconic(probe))
            {
                probe = NativeMethods.GetWindow(probe, NativeMethods.GW_HWNDPREV);
                continue;
            }

            NativeMethods.GetWindowThreadProcessId(probe, out var processId);
            if (processId == CurrentProcessId)
            {
                probe = NativeMethods.GetWindow(probe, NativeMethods.GW_HWNDPREV);
                continue;
            }

            if (!NativeMethods.GetWindowRect(probe, out var probeRect))
            {
                probe = NativeMethods.GetWindow(probe, NativeMethods.GW_HWNDPREV);
                continue;
            }

            if (NativeMethods.IntersectRect(out var intersection, ref targetRect, ref probeRect))
            {
                var intersectionWidth = Math.Max(0, intersection.Right - intersection.Left);
                var intersectionHeight = Math.Max(0, intersection.Bottom - intersection.Top);
                var intersectionArea = intersectionWidth * intersectionHeight;

                // Hide the pin if the window is 50% or more covered
                if (intersectionArea >= targetArea * 0.50)
                {
                    return false;
                }
            }

            probe = NativeMethods.GetWindow(probe, NativeMethods.GW_HWNDPREV);
        }

        return true;
    }

    internal static bool IsWindowObscuredAtPoint(IntPtr targetHwnd, IntPtr ignoreHwnd, NativeMethods.POINT pt)
    {
        // Walk Z-order from top to bottom
        var current = NativeMethods.GetWindow(NativeMethods.GetForegroundWindow(), NativeMethods.GW_HWNDFIRST);
        
        while (current != IntPtr.Zero)
        {
            if (current == ignoreHwnd)
            {
                current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
                continue;
            }

            if (!NativeMethods.IsWindowVisible(current) || NativeMethods.IsIconic(current))
            {
                current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
                continue;
            }

            // Check if window contains point
            if (NativeMethods.GetWindowRect(current, out var rect))
            {
                if (pt.X >= rect.Left && pt.X < rect.Right && pt.Y >= rect.Top && pt.Y < rect.Bottom)
                {
                    // Found the top-most visible window at this point.
                    // Is it our target?
                    if (current == targetHwnd)
                    {
                        return false; // Not obscured (it's the top one)
                    }

                    // Check if it's owned by target or owns target?
                    var root = NormalizeUpdateHwnd(current);
                    if (root == targetHwnd)
                    {
                        return false;
                    }

                    // It's some other window covering our target
                    return true;
                }
            }

            current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
        }

        // If we didn't find any window at that point (unlikely if target is there), assume not obscured
        return false;
    }

    internal static bool IsRectObscured(NativeMethods.RECT targetRect, IntPtr targetHwnd, IntPtr ignoreHwnd)
    {
        // Walk Z-order from top to bottom
        var current = NativeMethods.GetTopWindow(IntPtr.Zero);

        while (current != IntPtr.Zero)
        {
            if (current == ignoreHwnd)
            {
                current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
                continue;
            }

            if (current == targetHwnd)
            {
                // We reached the target window without hitting any obscuring window.
                return false;
            }

            // Check if it's owned by target or owns target?
            // If the current window is related to the target, we don't consider it an obstruction.
            var root = NormalizeUpdateHwnd(current);
            if (root == targetHwnd)
            {
                current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
                continue;
            }

            if (!NativeMethods.IsWindowVisible(current) || NativeMethods.IsIconic(current))
            {
                current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
                continue;
            }

            if (NativeMethods.GetWindowRect(current, out var currentRect))
            {
                if (NativeMethods.IntersectRect(out _, ref targetRect, ref currentRect))
                {
                    // Found a window above the target that intersects with the target rect.
                    return true;
                }
            }

            current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
        }

        return false;
    }
}
