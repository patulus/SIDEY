using Sidey.Platform.Windows.Deployment;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsUpdateCompletionTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"sidey-update-completion-{Guid.NewGuid():N}");

    [Fact]
    public void FreshInstallDoesNotReportAnUpdate()
    {
        WindowsUpdateCompletionTracker tracker = CreateTracker();

        Assert.Null(tracker.PendingNotificationVersion(
            onboardingCompleted: false,
            currentVersion: "1.3.2"));
        Assert.True(tracker.TryMarkLaunched("1.3.2"));
        Assert.Null(tracker.PendingNotificationVersion(
            onboardingCompleted: true,
            currentVersion: "1.3.2"));
    }

    [Fact]
    public void ExistingInstallWithoutARecordedVersionReportsTheCurrentUpdateOnce()
    {
        WindowsUpdateCompletionTracker tracker = CreateTracker();

        Assert.Equal(
            "1.3.2",
            tracker.PendingNotificationVersion(
                onboardingCompleted: true,
                currentVersion: "1.3.2"));
        Assert.True(tracker.TryMarkLaunched("1.3.2"));
        Assert.Null(tracker.PendingNotificationVersion(
            onboardingCompleted: true,
            currentVersion: "1.3.2"));
    }

    [Fact]
    public void VersionIncreaseReportsTheInstalledVersion()
    {
        WindowsUpdateCompletionTracker tracker = CreateTracker();
        Assert.True(tracker.TryMarkLaunched("1.3.1"));

        Assert.Equal(
            "1.3.2",
            tracker.PendingNotificationVersion(
                onboardingCompleted: true,
                currentVersion: "1.3.2"));
    }

    [Theory]
    [InlineData("1.3.2", "1.3.2")]
    [InlineData("1.3.2", "1.3.1")]
    public void SameVersionAndDowngradeDoNotReportAnUpdate(
        string previousVersion,
        string currentVersion)
    {
        WindowsUpdateCompletionTracker tracker = CreateTracker();
        Assert.True(tracker.TryMarkLaunched(previousVersion));

        Assert.Null(tracker.PendingNotificationVersion(
            onboardingCompleted: true,
            currentVersion: currentVersion));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("999999999999999999.0.0")]
    public void InvalidPreviousStateIsReplacedAfterOneNotification(string previousVersion)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath(), previousVersion);
        WindowsUpdateCompletionTracker tracker = CreateTracker();

        Assert.Equal(
            "1.3.2",
            tracker.PendingNotificationVersion(
                onboardingCompleted: true,
                currentVersion: "1.3.2"));
        Assert.True(tracker.TryMarkLaunched("1.3.2"));
        Assert.Equal("1.3.2", File.ReadAllText(StatePath()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private WindowsUpdateCompletionTracker CreateTracker() => new(StatePath());

    private string StatePath() => Path.Combine(_directory, "last-launched-version.txt");
}
