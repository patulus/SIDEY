using Sidey.Platform.Windows;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsShellSurfacePolicyTests
{
    [Theory]
    [InlineData("StartMenuExperienceHost", "Windows.UI.Core.CoreWindow")]
    [InlineData("SearchHost", "Windows.UI.Composition.DesktopWindowContentBridge")]
    [InlineData("ShellExperienceHost", "ControlCenterWindow")]
    [InlineData("WidgetBoard", "Windows.UI.Composition.DesktopWindowContentBridge")]
    [InlineData("MicrosoftStartFeedProvider", "Windows.UI.Composition.DesktopWindowContentBridge")]
    [InlineData("Widgets", "Windows.UI.Composition.DesktopWindowContentBridge")]
    [InlineData("explorer", "TopLevelWindowForOverflowXamlIsland")]
    [InlineData("explorer", "NotifyIconOverflowWindow")]
    [InlineData("explorer", "Shell_TrayWnd")]
    [InlineData("explorer", "Shell_SecondaryTrayWnd")]
    public void ForegroundShellSurfacesYieldOverlay(string processName, string windowClass)
    {
        Assert.True(WindowsShellSurfacePolicy.ShouldYield(processName, windowClass));
    }

    [Fact]
    public void VisibleTaskbarDoesNotOverrideNormalFullscreenForeground()
    {
        nint popupStyle = new(unchecked((long)0x80000000));
        nint toolWindowStyle = new(0x80);
        nint taskbar = 101;
        nint fullscreenWindow = 202;
        WindowsShellSurfaceResolver resolver = Resolver(
            [new FakeWindow(
                taskbar,
                "Shell_SecondaryTrayWnd",
                popupStyle,
                toolWindowStyle,
                IsVisible: true,
                IsCloaked: false,
                Width: 1920,
                Height: 48)],
            fullscreenWindow,
            foregroundShouldYield: false);

        Assert.Equal(nint.Zero, resolver.ForegroundSurface());
    }

    [Fact]
    public void VisibleTransientPopupYieldsBeforeNormalForeground()
    {
        nint popup = 101;
        WindowsShellSurfaceResolver resolver = Resolver(
            [new FakeWindow(
                popup,
                "#32768",
                nint.Zero,
                nint.Zero,
                IsVisible: true,
                IsCloaked: false,
                Width: 320,
                Height: 240)],
            foreground: 202,
            foregroundShouldYield: false);

        Assert.Equal(popup, resolver.ForegroundSurface());
    }

    [Fact]
    public void ApplicationTransientPopupsDoNotLowerOverlay()
    {
        nint popupStyle = new(unchecked((long)0x80000000));
        nint toolWindowStyle = new(0x80);
        WindowsShellSurfaceResolver resolver = Resolver(
            [
                new FakeWindow(
                    Handle: 101,
                    "#32768",
                    nint.Zero,
                    nint.Zero,
                    IsVisible: true,
                    IsCloaked: false,
                    Width: 320,
                    Height: 240,
                    ProcessName: "msedge"),
                new FakeWindow(
                    Handle: 102,
                    "Chrome_WidgetWin_1",
                    popupStyle,
                    toolWindowStyle,
                    IsVisible: true,
                    IsCloaked: false,
                    Width: 640,
                    Height: 480,
                    ProcessName: "msedge"),
            ],
            foreground: 202,
            foregroundShouldYield: false);

        Assert.Equal(nint.Zero, resolver.ForegroundSurface());
    }

    [Fact]
    public void VisibleWidgetRemainsAboveOverlayAfterForegroundChanges()
    {
        nint popupStyle = new(unchecked((long)0x80000000));
        nint toolWindowStyle = new(0x80);
        nint widget = 101;
        nint normalForeground = 202;
        WindowsShellSurfaceResolver resolver = Resolver(
            [
                new FakeWindow(
                    Handle: 100,
                    "ThumbnailDeviceHelperWnd",
                    popupStyle,
                    toolWindowStyle,
                    IsVisible: true,
                    IsCloaked: true,
                    Width: 1,
                    Height: 1),
                new FakeWindow(
                    widget,
                    "WindowsDashboard",
                    popupStyle,
                    toolWindowStyle,
                    IsVisible: true,
                    IsCloaked: false,
                    Width: 1084,
                    Height: 1736),
            ],
            foreground: normalForeground,
            foregroundShouldYield: false);

        Assert.Equal(widget, resolver.ForegroundSurface());
        Assert.Equal(widget, resolver.ForegroundSurface());
    }

    [Fact]
    public void VisibleTrayOverflowYieldsWithoutTakingForeground()
    {
        nint popupStyle = new(unchecked((long)0x80000000));
        nint toolWindowStyle = new(0x80);
        nint trayOverflow = 101;
        WindowsShellSurfaceResolver resolver = Resolver(
            [
                new FakeWindow(
                    Handle: 100,
                    "PseudoConsoleWindow",
                    popupStyle,
                    toolWindowStyle,
                    IsVisible: true,
                    IsCloaked: false,
                    Width: 0,
                    Height: 0),
                new FakeWindow(
                    trayOverflow,
                    "TopLevelWindowForOverflowXamlIsland",
                    popupStyle,
                    toolWindowStyle,
                    IsVisible: true,
                    IsCloaked: false,
                    Width: 480,
                    Height: 640),
            ],
            foreground: 202,
            foregroundShouldYield: false);

        Assert.Equal(trayOverflow, resolver.ForegroundSurface());
    }

    [Fact]
    public void ForegroundShellSurfaceYieldsWhenNoTransientPopupExists()
    {
        nint foreground = 202;
        WindowsShellSurfaceResolver resolver = Resolver(
            [],
            foreground,
            foregroundShouldYield: true);

        Assert.Equal(foreground, resolver.ForegroundSurface());
    }

    [Theory]
    [InlineData("ThumbnailDeviceHelperWnd", true, 1, 1)]
    [InlineData("PseudoConsoleWindow", false, 0, 0)]
    [InlineData("XamlExplorerHostIslandWindow_WASDK", false, 0, 0)]
    public void CloakedOrEmptyPopupWindowsDoNotOverrideNormalForeground(
        string windowClass,
        bool isCloaked,
        int width,
        int height)
    {
        nint popupStyle = new(unchecked((long)0x80000000));
        nint toolWindowStyle = new(0x80);
        WindowsShellSurfaceResolver resolver = Resolver(
            [new FakeWindow(
                Handle: 101,
                windowClass,
                popupStyle,
                toolWindowStyle,
                IsVisible: true,
                isCloaked,
                width,
                height)],
            foreground: 202,
            foregroundShouldYield: false);

        Assert.True(WindowsShellSurfacePolicy.IsTransientPopup(
            windowClass,
            popupStyle,
            toolWindowStyle));
        Assert.Equal(nint.Zero, resolver.ForegroundSurface());
    }

    [Theory]
    [InlineData("explorer", "CabinetWClass")]
    [InlineData("notepad", "Notepad")]
    [InlineData("SIDEY", "SIDEY.NativeOverlayWindow")]
    public void NormalApplicationWindowsKeepOverlayTopmost(string processName, string windowClass)
    {
        Assert.False(WindowsShellSurfacePolicy.ShouldYield(processName, windowClass));
    }

    [Theory]
    [InlineData("#32768")]
    [InlineData("Microsoft.UI.Content.PopupWindowSiteBridge")]
    [InlineData("Xaml_WindowedPopupClass")]
    [InlineData("tooltips_class32")]
    public void TransientMenusAndTooltipsCoverTheOverlay(string windowClass)
    {
        Assert.True(WindowsShellSurfacePolicy.IsTransientPopup(windowClass));
    }

    [Theory]
    [InlineData("WindowsForms10.Window.8.app.0.2bf8098_r6_ad1")]
    [InlineData("Qt663QWindowPopupDropShadowSaveBits")]
    [InlineData("CustomTrayMenu")]
    public void PopupToolWindowsCoverTheOverlayEvenWithApplicationSpecificClasses(string windowClass)
    {
        nint popupStyle = new(unchecked((long)0x80000000));
        nint toolWindowStyle = new(0x80);

        Assert.True(WindowsShellSurfacePolicy.IsTransientPopup(
            windowClass,
            popupStyle,
            toolWindowStyle));
    }

    [Theory]
    [InlineData("CabinetWClass")]
    [InlineData("Notepad")]
    [InlineData("SIDEY.NativeOverlayWindow")]
    public void NormalTopLevelWindowsAreNotTransientPopups(string windowClass)
    {
        Assert.False(WindowsShellSurfacePolicy.IsTransientPopup(windowClass));
    }

    [Theory]
    [InlineData("SIDEY.NativeOverlayWindow")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("WorkerW")]
    public void PersistentOverlayAndShellWindowsAreExcludedFromGenericPopupDetection(string windowClass)
    {
        nint popupStyle = new(unchecked((long)0x80000000));
        nint toolWindowStyle = new(0x80);

        Assert.False(WindowsShellSurfacePolicy.IsTransientPopup(
            windowClass,
            popupStyle,
            toolWindowStyle));
    }

    private static WindowsShellSurfaceResolver Resolver(
        IReadOnlyList<FakeWindow> visibleWindows,
        nint foreground,
        bool foregroundShouldYield) =>
        new(
            () =>
            {
                foreach (FakeWindow window in visibleWindows)
                {
                    if (WindowsShellSurfacePolicy.CanCoverOverlay(
                            window.IsVisible,
                            window.IsCloaked,
                            window.Width,
                            window.Height)
                        && WindowsShellSurfacePolicy.ShouldYieldTransientSurface(
                            window.ProcessName,
                            window.ClassName,
                            window.Style,
                            window.ExtendedStyle))
                    {
                        return window.Handle;
                    }
                }

                return nint.Zero;
            },
            () => foreground,
            window =>
            {
                Assert.Equal(foreground, window);
                return foregroundShouldYield;
            });

    private readonly record struct FakeWindow(
        nint Handle,
        string ClassName,
        nint Style,
        nint ExtendedStyle,
        bool IsVisible,
        bool IsCloaked,
        int Width,
        int Height,
        string ProcessName = "explorer");
}
