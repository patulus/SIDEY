using Sidey.Core.Domain;
using Sidey.Platform.Windows;

namespace Sidey.Platform.Windows.Tests;

public sealed class TrayMenuStateTests
{
    [Theory]
    [InlineData(false, true, 0x0000u)]
    [InlineData(true, true, 0x0008u)]
    [InlineData(false, false, 0x0001u)]
    [InlineData(true, false, 0x0009u)]
    public void NativeMenuFlagsUseTheWindowsCheckMark(
        bool isChecked,
        bool isEnabled,
        uint expected)
    {
        Assert.Equal(expected, TrayIconService.NativeMenuFlags(isChecked, isEnabled));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OverlayHiddenCheckStateIsCheckedOnlyWhileHidden(
        bool overlayVisible,
        bool expected)
    {
        Assert.Equal(expected, TrayIconService.OverlayHiddenCheckState(overlayVisible));
    }

    [Theory]
    [InlineData(AppThemePreference.System, 1)]
    [InlineData(AppThemePreference.Dark, 2)]
    [InlineData(AppThemePreference.Light, 3)]
    public void TrayMenuThemeMapsToTheRequestedWindowsAppMode(
        AppThemePreference theme,
        int expected)
    {
        Assert.Equal(expected, TrayIconService.PreferredAppModeValue(theme));
    }

    [Theory]
    [InlineData((int)TrayUpdateNotification.Latest, "", "최신 버전입니다.")]
    [InlineData((int)TrayUpdateNotification.Available, "1.2.2", "업데이트가 있습니다. 1.2.2")]
    [InlineData(
        (int)TrayUpdateNotification.Failed,
        "",
        "업데이트 확인에 실패했습니다. 잠시 후 다시 시도해 주세요.")]
    [InlineData(
        (int)TrayUpdateNotification.Installed,
        "1.3.2",
        "SIDEY 1.3.2 업데이트를 완료했습니다. 클릭해 변경 내용을 확인하세요.")]
    public void UpdateNotificationsUseTheLocalizedBody(
        int notification,
        string version,
        string expected)
    {
        Assert.Equal(
            expected,
            TrayIconService.UpdateNotificationBody((TrayUpdateNotification)notification, version));
    }

    [Theory]
    [InlineData((int)TrayUpdateNotification.Available, TrayCommand.Open)]
    [InlineData((int)TrayUpdateNotification.Latest, TrayCommand.Open)]
    [InlineData((int)TrayUpdateNotification.Failed, TrayCommand.Open)]
    [InlineData((int)TrayUpdateNotification.Installed, TrayCommand.ReleaseNotes)]
    public void CompletedUpdateNotificationOpensReleaseNotes(
        int notification,
        TrayCommand expected)
    {
        Assert.Equal(
            expected,
            TrayIconService.NotificationClickCommand((TrayUpdateNotification)notification));
    }
}
