using System.Runtime.InteropServices;
using Sidey.Core.Domain;

namespace Sidey.Platform.Windows.Shell;

public readonly record struct WindowsTaskbarPresentation(
    int EdgeInset,
    nint RevealedAutoHideWindow);

internal readonly record struct WindowsTaskbarWindow(
    nint Handle,
    NativePixelRect Bounds);

public static class WindowsTaskbarService
{
    private const int MinimumShownThickness = 8;
    private const int EdgeTolerance = 2;

    public static int VisibleInset(
        NativePixelRect monitorBounds,
        NativePixelRect workAreaBounds,
        OverlayEdge edge) =>
        VisiblePresentation(monitorBounds, workAreaBounds, edge).EdgeInset;

    public static WindowsTaskbarPresentation VisiblePresentation(
        NativePixelRect monitorBounds,
        NativePixelRect workAreaBounds,
        OverlayEdge edge)
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        IReadOnlyList<nint> autoHideWindows = GetAutoHideWindows(monitorBounds);
        return Presentation(
            monitorBounds,
            workAreaBounds,
            edge,
            GetVisibleTaskbarWindows(autoHideWindows),
            autoHideWindows);
    }

    public static int AdditionalInset(
        NativePixelRect monitorBounds,
        NativePixelRect workAreaBounds,
        OverlayEdge edge,
        IReadOnlyList<NativePixelRect> taskbarBounds)
    {
        var taskbars = new WindowsTaskbarWindow[taskbarBounds.Count];
        for (int index = 0; index < taskbarBounds.Count; index++)
        {
            taskbars[index] = new WindowsTaskbarWindow(nint.Zero, taskbarBounds[index]);
        }
        return Presentation(
            monitorBounds,
            workAreaBounds,
            edge,
            taskbars,
            autoHideWindows: []).EdgeInset;
    }

    internal static WindowsTaskbarPresentation Presentation(
        NativePixelRect monitorBounds,
        NativePixelRect workAreaBounds,
        OverlayEdge edge,
        IReadOnlyList<WindowsTaskbarWindow> taskbars,
        IReadOnlyList<nint> autoHideWindows)
    {
        if (!monitorBounds.IsValid || !workAreaBounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorBounds));
        }

        int inset = 0;
        nint revealedAutoHideWindow = nint.Zero;
        foreach (WindowsTaskbarWindow taskbar in taskbars)
        {
            int candidate = Inset(
                monitorBounds,
                workAreaBounds,
                edge,
                taskbar.Bounds);
            inset = Math.Max(inset, candidate);
            if (revealedAutoHideWindow == nint.Zero
                && Contains(autoHideWindows, taskbar.Handle)
                && IsShownTaskbar(monitorBounds, taskbar.Bounds))
            {
                revealedAutoHideWindow = taskbar.Handle;
            }
        }
        return new WindowsTaskbarPresentation(inset, revealedAutoHideWindow);
    }

    private static int Inset(
        NativePixelRect monitorBounds,
        NativePixelRect workAreaBounds,
        OverlayEdge edge,
        NativePixelRect taskbar)
    {
        if (!TryIntersect(monitorBounds, taskbar, out NativePixelRect visible))
        {
            return 0;
        }

        int candidate = edge switch
        {
            OverlayEdge.Bottom
                when Touches(visible.Y + visible.Height, monitorBounds.Y + monitorBounds.Height)
                     && IsHorizontal(visible)
                     && visible.Height >= MinimumShownThickness =>
                workAreaBounds.Y + workAreaBounds.Height - visible.Y,
            OverlayEdge.Top
                when Touches(visible.Y, monitorBounds.Y)
                     && IsHorizontal(visible)
                     && visible.Height >= MinimumShownThickness =>
                visible.Y + visible.Height - workAreaBounds.Y,
            OverlayEdge.Left
                when Touches(visible.X, monitorBounds.X)
                     && IsVertical(visible)
                     && visible.Width >= MinimumShownThickness =>
                visible.X + visible.Width - workAreaBounds.X,
            OverlayEdge.Right
                when Touches(visible.X + visible.Width, monitorBounds.X + monitorBounds.Width)
                     && IsVertical(visible)
                     && visible.Width >= MinimumShownThickness =>
                workAreaBounds.X + workAreaBounds.Width - visible.X,
            _ => 0,
        };
        return Math.Max(0, candidate);
    }

    private static bool IsShownTaskbar(
        NativePixelRect monitorBounds,
        NativePixelRect taskbar)
    {
        if (!TryIntersect(monitorBounds, taskbar, out NativePixelRect visible))
        {
            return false;
        }

        return IsHorizontal(visible)
            ? visible.Height >= MinimumShownThickness
                && (Touches(visible.Y, monitorBounds.Y)
                    || Touches(
                        visible.Y + visible.Height,
                        monitorBounds.Y + monitorBounds.Height))
            : IsVertical(visible)
                && visible.Width >= MinimumShownThickness
                && (Touches(visible.X, monitorBounds.X)
                    || Touches(
                        visible.X + visible.Width,
                        monitorBounds.X + monitorBounds.Width));
    }

    private static IReadOnlyList<WindowsTaskbarWindow> GetVisibleTaskbarWindows(
        IReadOnlyList<nint> autoHideWindows)
    {
        var taskbars = new List<WindowsTaskbarWindow>();
        AddVisibleTaskbar(NativeMethods.FindWindow("Shell_TrayWnd", null), taskbars);

        nint previous = nint.Zero;
        while (true)
        {
            nint taskbar = NativeMethods.FindWindowEx(
                nint.Zero,
                previous,
                "Shell_SecondaryTrayWnd",
                null);
            if (taskbar == nint.Zero)
            {
                break;
            }

            AddVisibleTaskbar(taskbar, taskbars);
            previous = taskbar;
        }
        foreach (nint autoHideWindow in autoHideWindows)
        {
            AddVisibleTaskbar(autoHideWindow, taskbars);
        }
        return taskbars;
    }

    private static void AddVisibleTaskbar(
        nint window,
        List<WindowsTaskbarWindow> taskbars)
    {
        if (window == nint.Zero)
        {
            return;
        }
        for (int index = 0; index < taskbars.Count; index++)
        {
            if (taskbars[index].Handle == window)
            {
                return;
            }
        }

        if (!NativeMethods.IsWindowVisible(window)
            || !NativeMethods.GetWindowRect(window, out NativeRect rectangle))
        {
            return;
        }

        int width = rectangle.Right - rectangle.Left;
        int height = rectangle.Bottom - rectangle.Top;
        if (width > 0 && height > 0)
        {
            taskbars.Add(new WindowsTaskbarWindow(
                window,
                new NativePixelRect(rectangle.Left, rectangle.Top, width, height)));
        }
    }

    private static IReadOnlyList<nint> GetAutoHideWindows(NativePixelRect monitorBounds)
    {
        const uint AbmGetAutoHideBarEx = 0x0000000B;
        var windows = new List<nint>(capacity: 4);
        for (uint edge = 0; edge < 4; edge++)
        {
            var data = new NativeMethods.AppBarData
            {
                _size = (uint)Marshal.SizeOf<NativeMethods.AppBarData>(),
                _edge = edge,
                _bounds = new NativeRect
                {
                    Left = monitorBounds.X,
                    Top = monitorBounds.Y,
                    Right = monitorBounds.X + monitorBounds.Width,
                    Bottom = monitorBounds.Y + monitorBounds.Height,
                },
            };
            nint window = unchecked((nint)NativeMethods.SHAppBarMessage(
                AbmGetAutoHideBarEx,
                ref data));
            if (window != nint.Zero && !Contains(windows, window))
            {
                windows.Add(window);
            }
        }
        return windows;
    }

    private static bool Contains(IReadOnlyList<nint> windows, nint window)
    {
        for (int index = 0; index < windows.Count; index++)
        {
            if (windows[index] == window)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryIntersect(
        NativePixelRect first,
        NativePixelRect second,
        out NativePixelRect intersection)
    {
        int left = Math.Max(first.X, second.X);
        int top = Math.Max(first.Y, second.Y);
        int right = Math.Min(first.X + first.Width, second.X + second.Width);
        int bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        intersection = new NativePixelRect(left, top, right - left, bottom - top);
        return intersection.IsValid;
    }

    private static bool Touches(int first, int second) =>
        Math.Abs(first - second) <= EdgeTolerance;

    private static bool IsHorizontal(NativePixelRect bounds) => bounds.Width > bounds.Height;

    private static bool IsVertical(NativePixelRect bounds) => bounds.Height > bounds.Width;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct AppBarData
        {
            internal uint _size;
            internal nint _window;
            internal uint _callbackMessage;
            internal uint _edge;
            internal NativeRect _bounds;
            internal nint _parameter;
        }

        [DllImport("shell32.dll")]
        internal static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
        public static extern nint FindWindow(string className, string? windowName);

        [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
        public static extern nint FindWindowEx(
            nint parent,
            nint childAfter,
            string className,
            string? windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    }
}
