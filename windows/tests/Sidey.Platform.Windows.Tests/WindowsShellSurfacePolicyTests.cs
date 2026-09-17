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
                toolWindowStyle)],
            fullscreenWindow,
            foregroundShouldYield: false);

        Assert.Equal(nint.Zero, resolver.ForegroundSurface());
    }

    [Fact]
    public void VisibleTransientPopupYieldsBeforeNormalForeground()
    {
        nint popup = 101;
        WindowsShellSurfaceResolver resolver = Resolver(
            [new FakeWindow(popup, "#32768", nint.Zero, nint.Zero)],
            foreground: 202,
            foregroundShouldYield: false);

        Assert.Equal(popup, resolver.ForegroundSurface());
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
            shouldYield =>
            {
                foreach (FakeWindow window in visibleWindows)
                {
                    if (shouldYield(window.ClassName, window.Style, window.ExtendedStyle))
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
        nint ExtendedStyle);
}
