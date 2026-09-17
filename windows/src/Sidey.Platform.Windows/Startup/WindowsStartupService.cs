using Microsoft.Win32;

namespace Sidey.Platform.Windows.Startup;

public interface IWindowsStartupService
{
    public bool IsEnabled();

    public void SetEnabled(bool enabled);

    public void UpgradeEnabledRegistration();
}

public sealed class WindowsStartupService : IWindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SIDEY";
    public const string BackgroundLaunchArgument = "--background";
    public const string UpdateShutdownArgument = "--shutdown-for-update";

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value
            && (StringComparer.OrdinalIgnoreCase.Equals(value, StartupCommand())
                || StringComparer.OrdinalIgnoreCase.Equals(value, QuotedExecutablePath()));
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows startup registration is required.");
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, StartupCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public void UpgradeEnabledRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key.GetValue(ValueName) is string value
            && StringComparer.OrdinalIgnoreCase.Equals(value, QuotedExecutablePath()))
        {
            key.SetValue(ValueName, StartupCommand(), RegistryValueKind.String);
        }
    }

    public static bool IsBackgroundLaunch(string? arguments) =>
        IsDedicatedArgument(arguments, BackgroundLaunchArgument);

    public static bool IsUpdateShutdown(string? arguments) =>
        IsDedicatedArgument(arguments, UpdateShutdownArgument);

    private static bool IsDedicatedArgument(string? arguments, string expected) =>
        StringComparer.OrdinalIgnoreCase.Equals(arguments?.Trim(), expected);

    private static string StartupCommand() =>
        $"{QuotedExecutablePath()} {BackgroundLaunchArgument}";

    private static string QuotedExecutablePath() =>
        $"\"{SideyDeploymentPaths.LauncherPath()}\"";
}
