namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsStartupServiceTests
{
    [Theory]
    [InlineData("--background", true)]
    [InlineData("  --background  ", true)]
    [InlineData("--BACKGROUND", true)]
    [InlineData("", false)]
    [InlineData("--background --extra", false)]
    [InlineData(null, false)]
    public void BackgroundLaunchRequiresTheDedicatedStartupArgument(
        string? arguments,
        bool expected)
    {
        Assert.Equal(expected, WindowsStartupService.IsBackgroundLaunch(arguments));
    }

    [Theory]
    [InlineData("--shutdown-for-update", true)]
    [InlineData("  --shutdown-for-update  ", true)]
    [InlineData("--SHUTDOWN-FOR-UPDATE", true)]
    [InlineData("", false)]
    [InlineData("--shutdown-for-update --extra", false)]
    [InlineData(null, false)]
    public void UpdateShutdownRequiresTheDedicatedInternalArgument(
        string? arguments,
        bool expected)
    {
        Assert.Equal(expected, WindowsStartupService.IsUpdateShutdown(arguments));
    }

    [Fact]
    public void StructuredRuntimeResolvesThePublicLauncher()
    {
        string root = Path.Combine(Path.GetTempPath(), "sidey-deployment-root");
        string runtime = Path.Combine(root, "Runtime");

        Assert.Equal(
            root,
            SideyDeploymentPaths.DeploymentRoot(
                Path.Combine(runtime, "SIDEY.Host.exe"),
                runtime));
        Assert.Equal(
            Path.Combine(root, "SIDEY.exe"),
            SideyDeploymentPaths.LauncherPath(
                Path.Combine(runtime, "SIDEY.Host.exe"),
                runtime));
    }
}
