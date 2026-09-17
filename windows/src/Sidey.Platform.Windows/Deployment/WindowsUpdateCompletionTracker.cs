using Sidey.Core.Storage;

namespace Sidey.Platform.Windows.Deployment;

public sealed class WindowsUpdateCompletionTracker
{
    private readonly string _path;

    public WindowsUpdateCompletionTracker(string? path = null)
    {
        _path = path ?? Path.Combine(
            SideyStoragePaths.LocalApplicationDataRoot(),
            "SIDEY",
            "last-launched-version.txt");
    }

    public string? PendingNotificationVersion(
        bool onboardingCompleted,
        string currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        _ = WindowsUpdateService.IsNewerVersion(currentVersion, currentVersion);
        if (!onboardingCompleted)
        {
            return null;
        }

        string? previousVersion = ReadPreviousVersion();
        if (string.IsNullOrWhiteSpace(previousVersion))
        {
            return currentVersion;
        }

        try
        {
            return WindowsUpdateService.IsNewerVersion(currentVersion, previousVersion)
                ? currentVersion
                : null;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or FormatException
            or OverflowException)
        {
            return currentVersion;
        }
    }

    public bool TryMarkLaunched(string currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        _ = WindowsUpdateService.IsNewerVersion(currentVersion, currentVersion);
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The update completion state folder is invalid.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, currentVersion);
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private string? ReadPreviousVersion()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
