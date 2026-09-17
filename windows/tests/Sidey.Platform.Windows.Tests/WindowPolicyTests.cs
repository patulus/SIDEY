
using Sidey.Core.Domain;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowPolicyTests
{
    private const uint Topmost = 0x00000008;
    private const uint Transparent = 0x00000020;
    private const uint ToolWindow = 0x00000080;
    private const uint Layered = 0x00080000;
    private const uint NoRedirectionBitmap = 0x00200000;
    private const uint NoActivate = 0x08000000;

    [Fact]
    public void WorldWindowIsLayeredClickThroughTopmostAndNonActivating()
    {
        uint style = NativeOverlayWindow.ExtendedStyleBits(NativeOverlayWindowRole.World);

        AssertHas(style, Topmost);
        AssertHas(style, Transparent);
        AssertHas(style, ToolWindow);
        AssertHas(style, Layered);
        Assert.Equal(0u, style & NoRedirectionBitmap);
        AssertHas(style, NoActivate);
    }

    [Fact]
    public void HotspotReceivesInputButNeverActivatesOrAppearsInTaskSwitcher()
    {
        uint style = NativeOverlayWindow.ExtendedStyleBits(NativeOverlayWindowRole.Hotspot);

        AssertHas(style, Topmost);
        AssertHas(style, ToolWindow);
        AssertHas(style, Layered);
        AssertHas(style, NoActivate);
        Assert.Equal(0u, style & Transparent);
        Assert.Equal(0u, style & NoRedirectionBitmap);
    }

    [Fact]
    public void ProductMinimumOsIsWindowsTen1809()
    {
        Assert.Equal(17763, WindowsVersionGuard.MinimumBuild);
    }

    [Theory]
    [InlineData("true", "1", true)]
    [InlineData("TRUE", "1", true)]
    [InlineData("false", "1", false)]
    [InlineData(null, "1", false)]
    [InlineData("true", "0", false)]
    [InlineData("true", null, false)]
    public void StartupSmokeOverrideRequiresCiAndExplicitOptIn(
        string? ci,
        string? startupSmoke,
        bool expected)
    {
        Assert.Equal(expected, WindowsVersionGuard.IsCiStartupSmokeOverride(ci, startupSmoke));
    }

    [Fact]
    public void CurrentWindowsSourceVersionIsOneThreeOne()
    {
        Assert.Equal("1.3.1", WindowsUpdateService.CurrentVersion);
    }

    [Theory]
    [InlineData(0, 0, 52, 52, true)]
    [InlineData(-100, -100, 52, 52, true)]
    [InlineData(0, 0, 0, 52, false)]
    [InlineData(0, 0, 52, -1, false)]
    public void PixelRectSupportsNegativeMonitorCoordinatesButRejectsEmptySize(
        int x,
        int y,
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(expected, new NativePixelRect(x, y, width, height).IsValid);
    }

    [Theory]
    [InlineData(OverlayEdge.Bottom, OverlaySpan.Full, -1920, 500, 1920, 300)]
    [InlineData(OverlayEdge.Top, OverlaySpan.Half, -1440, -200, 960, 300)]
    [InlineData(OverlayEdge.Left, OverlaySpan.Third, -1920, 133, 300, 333)]
    [InlineData(OverlayEdge.Right, OverlaySpan.Full, -300, -200, 300, 1000)]
    public void WindowsRegionLayoutUsesTopLeftPixelCoordinates(
        OverlayEdge edge,
        OverlaySpan span,
        int x,
        int y,
        int width,
        int height)
    {
        var workArea = new NativePixelRect(-1920, -200, 1920, 1000);

        NativePixelRect frame = WindowsOverlayRegionLayout.Frame(
            workArea,
            120,
            new OverlayRegionPreference(edge, span, null));

        Assert.Equal(new NativePixelRect(x, y, width, height), frame);
    }

    [Fact]
    public void AllTwelveWindowsPresetsStayInsideTheWorkAreaAndTouchTheSelectedEdge()
    {
        var workArea = new NativePixelRect(-1920, -200, 1920, 1000);

        foreach (OverlayEdge edge in Enum.GetValues<OverlayEdge>())
        {
            foreach (OverlaySpan span in Enum.GetValues<OverlaySpan>())
            {
                NativePixelRect frame = WindowsOverlayRegionLayout.Frame(
                    workArea,
                    120,
                    new OverlayRegionPreference(edge, span, null));

                Assert.True(frame.IsValid);
                Assert.InRange(frame.X, workArea.X, workArea.X + workArea.Width);
                Assert.InRange(frame.Y, workArea.Y, workArea.Y + workArea.Height);
                Assert.InRange(frame.X + frame.Width, workArea.X, workArea.X + workArea.Width);
                Assert.InRange(frame.Y + frame.Height, workArea.Y, workArea.Y + workArea.Height);

                if (edge is OverlayEdge.Bottom or OverlayEdge.Top)
                {
                    Assert.Equal(
                        Round(workArea.Width * span.Fraction()),
                        frame.Width);
                    Assert.Equal(300, frame.Height);
                    Assert.Equal(
                        edge == OverlayEdge.Top
                            ? workArea.Y
                            : workArea.Y + workArea.Height,
                        edge == OverlayEdge.Top ? frame.Y : frame.Y + frame.Height);
                }
                else
                {
                    Assert.Equal(300, frame.Width);
                    Assert.Equal(
                        Round(workArea.Height * span.Fraction()),
                        frame.Height);
                    Assert.Equal(
                        edge == OverlayEdge.Left
                            ? workArea.X
                            : workArea.X + workArea.Width,
                        edge == OverlayEdge.Left ? frame.X : frame.X + frame.Width);
                }
            }
        }
    }

    [Fact]
    public void RenderFrameAddsReactionRoomWithoutChangingTheActivityPreset()
    {
        var workArea = new NativePixelRect(0, 0, 1920, 1080);
        var preference = new OverlayRegionPreference(OverlayEdge.Bottom, OverlaySpan.Half, null);

        WindowsOverlayRegionFrames frames = WindowsOverlayRegionLayout.Frames(workArea, 96, preference);

        Assert.Equal(new NativePixelRect(480, 840, 960, 240), frames.ActivityFrame);
        Assert.Equal(new NativePixelRect(336, 720, 1248, 360), frames.RenderFrame);
        Assert.Equal(frames.ActivityFrame, WindowsOverlayRegionLayout.Frame(workArea, 96, preference));
    }

    [Fact]
    public void ShownAutoHideTaskbarMovesTheBottomTrackAboveIt()
    {
        var monitor = new NativePixelRect(0, 0, 1920, 1080);
        NativePixelRect fullScreenWorkArea = monitor;

        Assert.Equal(
            48,
            WindowsTaskbarService.AdditionalInset(
                monitor,
                fullScreenWorkArea,
                OverlayEdge.Bottom,
                [new NativePixelRect(0, 1032, 1920, 48)]));
        Assert.Equal(
            0,
            WindowsTaskbarService.AdditionalInset(
                monitor,
                fullScreenWorkArea,
                OverlayEdge.Bottom,
                [new NativePixelRect(0, 1078, 1920, 2)]));
    }

    [Fact]
    public void RevealedAutoHideTaskbarCoversOverlayWhileCharactersMoveAboveIt()
    {
        var monitor = new NativePixelRect(0, 0, 1920, 1080);
        nint taskbar = 101;

        WindowsTaskbarPresentation presentation = WindowsTaskbarService.Presentation(
            monitor,
            monitor,
            OverlayEdge.Bottom,
            [new WindowsTaskbarWindow(taskbar, new NativePixelRect(0, 1032, 1920, 48))],
            autoHideWindows: [taskbar]);

        Assert.Equal(48, presentation.EdgeInset);
        Assert.Equal(taskbar, presentation.RevealedAutoHideWindow);
    }

    [Fact]
    public void HiddenAutoHideTaskbarRestoresOverlayTopmostAtTheScreenEdge()
    {
        var monitor = new NativePixelRect(0, 0, 1920, 1080);
        nint taskbar = 101;

        WindowsTaskbarPresentation presentation = WindowsTaskbarService.Presentation(
            monitor,
            monitor,
            OverlayEdge.Bottom,
            [new WindowsTaskbarWindow(taskbar, new NativePixelRect(0, 1078, 1920, 2))],
            autoHideWindows: [taskbar]);

        Assert.Equal(0, presentation.EdgeInset);
        Assert.Equal(nint.Zero, presentation.RevealedAutoHideWindow);
    }

    [Fact]
    public void FixedTaskbarMovesCharactersWithoutLoweringOverlay()
    {
        var monitor = new NativePixelRect(0, 0, 1920, 1080);

        WindowsTaskbarPresentation presentation = WindowsTaskbarService.Presentation(
            monitor,
            monitor,
            OverlayEdge.Bottom,
            [new WindowsTaskbarWindow(101, new NativePixelRect(0, 1032, 1920, 48))],
            autoHideWindows: []);

        Assert.Equal(48, presentation.EdgeInset);
        Assert.Equal(nint.Zero, presentation.RevealedAutoHideWindow);
    }

    [Fact]
    public void AutoHideTaskbarOnAnotherMonitorDoesNotLowerThisOverlay()
    {
        var monitor = new NativePixelRect(0, 0, 1920, 1080);
        nint otherMonitorTaskbar = 202;

        WindowsTaskbarPresentation presentation = WindowsTaskbarService.Presentation(
            monitor,
            monitor,
            OverlayEdge.Bottom,
            [
                new WindowsTaskbarWindow(101, new NativePixelRect(0, 1032, 1920, 48)),
                new WindowsTaskbarWindow(otherMonitorTaskbar, new NativePixelRect(1920, 1032, 1920, 48)),
            ],
            autoHideWindows: [otherMonitorTaskbar]);

        Assert.Equal(48, presentation.EdgeInset);
        Assert.Equal(nint.Zero, presentation.RevealedAutoHideWindow);
    }

    [Fact]
    public void RevealedAutoHideTaskbarOnAnotherEdgeStillCoversOverlay()
    {
        var monitor = new NativePixelRect(0, 0, 1920, 1080);
        nint taskbar = 101;

        WindowsTaskbarPresentation presentation = WindowsTaskbarService.Presentation(
            monitor,
            monitor,
            OverlayEdge.Bottom,
            [new WindowsTaskbarWindow(taskbar, new NativePixelRect(0, 0, 48, 1080))],
            autoHideWindows: [taskbar]);

        Assert.Equal(0, presentation.EdgeInset);
        Assert.Equal(taskbar, presentation.RevealedAutoHideWindow);
    }

    [Fact]
    public void WorkAreaThatAlreadyReservesTheTaskbarDoesNotGetInsetTwice()
    {
        var monitor = new NativePixelRect(0, 0, 1920, 1080);

        Assert.Equal(
            0,
            WindowsTaskbarService.AdditionalInset(
                monitor,
                new NativePixelRect(0, 0, 1920, 1032),
                OverlayEdge.Bottom,
                [new NativePixelRect(0, 1032, 1920, 48)]));
    }

    [Fact]
    public void HorizontalTaskbarNeverCreatesLeftOrRightMargin()
    {
        var monitor = new NativePixelRect(0, 0, 2560, 1600);
        var taskbar = new NativePixelRect(0, 1540, 2560, 60);

        Assert.Equal(0, WindowsTaskbarService.AdditionalInset(
            monitor, monitor, OverlayEdge.Left, [taskbar]));
        Assert.Equal(0, WindowsTaskbarService.AdditionalInset(
            monitor, monitor, OverlayEdge.Right, [taskbar]));
    }

    [Fact]
    public void VerticalTaskbarOnlyInsetsItsOwnEdge()
    {
        var monitor = new NativePixelRect(0, 0, 2560, 1600);
        var taskbar = new NativePixelRect(0, 0, 72, 1600);

        Assert.Equal(72, WindowsTaskbarService.AdditionalInset(
            monitor, monitor, OverlayEdge.Left, [taskbar]));
        Assert.Equal(0, WindowsTaskbarService.AdditionalInset(
            monitor, monitor, OverlayEdge.Bottom, [taskbar]));
    }

    [Theory]
    [InlineData("0.3.0-alpha.2", "0.3.0-alpha.1", true)]
    [InlineData("0.3.0-alpha.10", "0.3.0-alpha.2", true)]
    [InlineData("0.3.0", "0.3.0-alpha.10", true)]
    [InlineData("0.2.0", "0.3.0-alpha.1", false)]
    [InlineData("0.3.0-alpha.1", "0.3.0-alpha.1", false)]
    public void UpdateComparisonNeverOffersSameVersionOrDowngrade(
        string candidate,
        string current,
        bool expected)
    {
        Assert.Equal(expected, WindowsUpdateService.IsNewerVersion(candidate, current));
    }

    private static void AssertHas(uint style, uint flag) => Assert.Equal(flag, style & flag);

    private static int Round(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
