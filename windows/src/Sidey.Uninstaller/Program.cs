using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Sidey.Uninstaller
{
    public static class Program
    {
        private const string CleanupArgument = "--cleanup";
        private const string CleanupCredentialsArgument = "--cleanup-credentials";
        private const string CleanupLocalDataArgument = "--cleanup-local-data";
        private const string CleanupStartupArgument = "--cleanup-startup";
        private const string CleanupCredentialsAsDesktopUserArgument =
            "--cleanup-credentials-as-desktop-user";
        private const string CleanupLocalDataAsDesktopUserArgument =
            "--cleanup-local-data-as-desktop-user";
        private const string CleanupStartupAsDesktopUserArgument =
            "--cleanup-startup-as-desktop-user";
        private const string LaunchSideyAsDesktopUserArgument =
            "--launch-sidey-as-desktop-user";
        private const string EnableStartupAsDesktopUserArgument =
            "--enable-startup-as-desktop-user";
        private const string EnableStartupArgument = "--enable-startup";
        private const string RequestShutdownAsDesktopUserArgument =
            "--request-shutdown-as-desktop-user";
        private const string RequestShutdownArgument = "--request-shutdown";
        private const string UpdateShutdownArgument = "--shutdown-for-update";
        private const string BackgroundLaunchArgument = "--background";
        private const string LegacyMsiDetectArgument = "--detect-legacy-msi";
        private const string StopSideyProcessesArgument = "--stop-sidey-processes";
        private const string CredentialFilter = "SIDEY/*";
        private const string StartupRegistryPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "SIDEY";
        private const string InstallerRegistryPath = @"Software\SIDEY\Installer";
        private const string InstallerLanguageValueName = "Language";
        private const string UpgradeCode = "{E744D02B-C3CF-41CE-A4C9-9BA1EB10C6B9}";
        private const int EnglishLanguage = 1033;
        private const int JapaneseLanguage = 1041;
        private const int KoreanLanguage = 1042;
        private const int RussianLanguage = 1049;
        private const int UkrainianLanguage = 1058;
        private const int SimplifiedChineseLanguage = 2052;
        private const int TraditionalChineseLanguage = 1028;
        private const int ErrorNotFound = 1168;
        private const int ErrorProductNotInstalled = 1605;
        private const int ErrorAccessDenied = 5;
        private const int ErrorInvalidParameter = 87;
        // The customer-defined bit keeps helper-owned state failures distinct
        // from Win32 codes returned by the native desktop-user launch path.
        private const int HelperErrorDesktopUnavailable = 0x20000001;
        private const int HelperErrorNativeCodeUnavailable = 0x20000002;
        private const int HelperErrorProcessNotStarted = 0x20000003;
        private const int HelperErrorUnexpectedFailure = 0x20000004;
        private const uint ErrorSuccess = 0;
        private const uint ErrorNoMoreItems = 259;

        [STAThread]
        public static int Main(string[] arguments)
        {
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    LaunchSideyAsDesktopUserArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return LaunchSideyAsDesktopUser();
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    EnableStartupAsDesktopUserArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RunThisHelperAsDesktopUser(EnableStartupArgument);
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    RequestShutdownAsDesktopUserArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RunThisHelperAsDesktopUser(RequestShutdownArgument);
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    CleanupLocalDataAsDesktopUserArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RunThisHelperAsDesktopUser(CleanupLocalDataArgument);
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    CleanupCredentialsAsDesktopUserArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RunThisHelperAsDesktopUser(CleanupCredentialsArgument);
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    CleanupStartupAsDesktopUserArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RunThisHelperAsDesktopUser(CleanupStartupArgument);
            }
            if (arguments.Length == 1
                && string.Equals(arguments[0], CleanupArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (!DesktopUserProcess.IsCurrentDesktopUser())
                {
                    return 5;
                }
                int localDataResult = RemoveCurrentUserData();
                int credentialsResult = RemoveCurrentUserCredentials();
                return localDataResult != 0 ? localDataResult : credentialsResult;
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    CleanupLocalDataArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!DesktopUserProcess.IsCurrentDesktopUser())
                {
                    return 5;
                }
                return RemoveCurrentUserData();
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    CleanupCredentialsArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!DesktopUserProcess.IsCurrentDesktopUser())
                {
                    return 5;
                }
                return RemoveCurrentUserCredentials();
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    CleanupStartupArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!DesktopUserProcess.IsCurrentDesktopUser())
                {
                    return 5;
                }
                return RemoveCurrentUserStartupRegistration();
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    EnableStartupArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!DesktopUserProcess.IsCurrentDesktopUser())
                {
                    return 5;
                }
                return EnableCurrentUserStartupRegistration();
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    RequestShutdownArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!DesktopUserProcess.IsCurrentDesktopUser())
                {
                    return 5;
                }
                return RequestCurrentUserSideyShutdown();
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    LegacyMsiDetectArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return DetectLegacyMsi();
            }
            if (arguments.Length == 1
                && string.Equals(
                    arguments[0],
                    StopSideyProcessesArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return StopSideyProcesses();
            }
            if (arguments.Length != 0)
            {
                return 64;
            }

            try
            {
                string productCode = FindInstalledProductCode();
                if (string.IsNullOrEmpty(productCode))
                {
                    ShowLegacyUninstallMessage(
                        LegacyUninstallMessage.NotInstalled,
                        null,
                        0x30);
                    return 2;
                }

                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string installerPath = Path.Combine(systemDirectory, "msiexec.exe");
                var start = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/x " + QuoteArgument(productCode),
                    UseShellExecute = true,
                    Verb = "runas",
                };
                Process.Start(start);

                // Do not wait here. The installed helper must exit before MSI
                // removes it and the rest of the legacy installation folder.
                return 0;
            }
            catch (Exception exception)
            {
                ShowLegacyUninstallMessage(
                    LegacyUninstallMessage.InstallerStartFailed,
                    exception,
                    0x10);
                return 1;
            }
        }

        private static int LaunchSideyAsDesktopUser()
        {
            try
            {
                string installDirectory;
                string launcherPath;
                if (!TryGetDeploymentPaths(out installDirectory, out launcherPath))
                {
                    return 2;
                }

                return DesktopUserProcess.Start(
                    launcherPath,
                    string.Empty,
                    installDirectory,
                    waitForExit: false);
            }
            catch (Exception exception)
            {
                return GetDesktopUserRunnerErrorCode(exception);
            }
        }

        private static int EnableCurrentUserStartupRegistration()
        {
            try
            {
                string installDirectory;
                string launcherPath;
                if (!TryGetDeploymentPaths(out installDirectory, out launcherPath))
                {
                    return 2;
                }

                string startupCommand = "\"" + launcherPath + "\" " + BackgroundLaunchArgument;
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    StartupRegistryPath,
                    writable: true))
                {
                    key.SetValue(StartupValueName, startupCommand, RegistryValueKind.String);
                }
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static int RequestCurrentUserSideyShutdown()
        {
            try
            {
                if (!IsSideyRunning())
                {
                    return 0;
                }

                string installDirectory;
                string launcherPath;
                if (!TryGetDeploymentPaths(out installDirectory, out launcherPath))
                {
                    return 2;
                }

                using (Process launcher = Process.Start(new ProcessStartInfo
                {
                    FileName = launcherPath,
                    Arguments = UpdateShutdownArgument,
                    WorkingDirectory = installDirectory,
                    UseShellExecute = false,
                }))
                {
                    if (launcher == null)
                    {
                        return HelperErrorProcessNotStarted;
                    }
                    launcher.WaitForExit(5000);
                }

                Stopwatch timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(5))
                {
                    if (!IsSideyRunning())
                    {
                        return 0;
                    }
                    Thread.Sleep(100);
                }

                // Older versions do not understand the private shutdown request.
                // Setup follows this grace period with its existing bounded force-stop.
                return 0;
            }
            catch (Exception exception)
            {
                return GetDesktopUserRunnerErrorCode(exception);
            }
        }

        private static bool IsSideyRunning()
        {
            foreach (string processName in new[] { "SIDEY", "SIDEY.Host" })
            {
                Process[] processes = Process.GetProcessesByName(processName);
                bool running = false;
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        if (!process.HasExited)
                        {
                            running = true;
                        }
                    }
                }
                if (running)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetDeploymentPaths(
            out string installDirectory,
            out string launcherPath)
        {
            string helperPath = Process.GetCurrentProcess().MainModule.FileName;
            DirectoryInfo runtimeDirectory = Directory.GetParent(helperPath);
            DirectoryInfo deploymentDirectory = runtimeDirectory == null
                ? null
                : runtimeDirectory.Parent;
            if (deploymentDirectory == null)
            {
                installDirectory = null;
                launcherPath = null;
                return false;
            }

            installDirectory = deploymentDirectory.FullName;
            launcherPath = Path.Combine(installDirectory, "SIDEY.exe");
            return File.Exists(launcherPath);
        }

        private static int RunThisHelperAsDesktopUser(string argument)
        {
            try
            {
                string helperPath = Process.GetCurrentProcess().MainModule.FileName;
                return DesktopUserProcess.Start(
                    helperPath,
                    QuoteArgument(argument),
                    Path.GetDirectoryName(helperPath),
                    waitForExit: true);
            }
            catch (Exception exception)
            {
                return GetDesktopUserRunnerErrorCode(exception);
            }
        }

        private static int GetDesktopUserRunnerErrorCode(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                var win32Exception = current as System.ComponentModel.Win32Exception;
                if (win32Exception != null && win32Exception.NativeErrorCode != 0)
                {
                    return win32Exception.NativeErrorCode;
                }

                uint hresult = unchecked((uint)current.HResult);
                if ((hresult & 0xFFFF0000u) == 0x80070000u)
                {
                    int win32Error = unchecked((int)(hresult & 0x0000FFFFu));
                    if (win32Error != 0)
                    {
                        return win32Error;
                    }
                }

                if (current is UnauthorizedAccessException)
                {
                    return ErrorAccessDenied;
                }
                if (current is ArgumentException)
                {
                    return ErrorInvalidParameter;
                }
            }

            return HelperErrorUnexpectedFailure;
        }

        private static int DetectLegacyMsi()
        {
            try
            {
                return string.IsNullOrEmpty(FindInstalledProductCode())
                    ? ErrorProductNotInstalled
                    : 0;
            }
            catch
            {
                return 1;
            }
        }

        private static int StopSideyProcesses()
        {
            bool failed = false;
            foreach (string processName in new[] { "SIDEY", "SIDEY.Host" })
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                    failed = true;
                    continue;
                }

                foreach (Process process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            if (process.HasExited)
                            {
                                continue;
                            }
                            process.Kill();
                            if (!process.WaitForExit(30000))
                            {
                                failed = true;
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // The process can exit between enumeration and Kill.
                            // Treat that race as success, but keep genuine failures.
                            if (!HasExited(process))
                            {
                                failed = true;
                            }
                        }
                        catch
                        {
                            failed = true;
                        }
                    }
                }
            }

            return failed ? 5 : 0;
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static string FindInstalledProductCode()
        {
            StringBuilder productCode = new StringBuilder(39);
            uint result = MsiEnumRelatedProducts(UpgradeCode, 0, 0, productCode);
            if (result == ErrorNoMoreItems)
            {
                return null;
            }
            if (result != ErrorSuccess)
            {
                throw new System.ComponentModel.Win32Exception(
                    unchecked((int)result),
                    "Windows Installer product lookup failed.");
            }

            return productCode.ToString();
        }

        private static int RemoveCurrentUserData()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    throw new InvalidOperationException("Local application data is unavailable.");
                }

                string normalizedLocalAppData = Path.GetFullPath(localAppData)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string dataRoot = Path.GetFullPath(Path.Combine(normalizedLocalAppData, "SIDEY"));
                DirectoryInfo parent = Directory.GetParent(dataRoot);
                if (parent == null
                    || !string.Equals(
                        parent.FullName.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        normalizedLocalAppData,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Unsafe SIDEY data directory.");
                }

                if (Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, true);
                }

                return 0;
            }
            catch
            {
                return 3;
            }
        }

        private static int RemoveCurrentUserCredentials()
        {
            try
            {
                DeleteSideyCredentials();
                return 0;
            }
            catch
            {
                return 3;
            }
        }

        private static int RemoveCurrentUserStartupRegistration()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    StartupRegistryPath,
                    writable: true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(StartupValueName, throwOnMissingValue: false);
                    }
                }
                return 0;
            }
            catch
            {
                return 3;
            }
        }

        private static void DeleteSideyCredentials()
        {
            int count;
            IntPtr credentials;
            if (!CredEnumerate(CredentialFilter, 0, out count, out credentials))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    return;
                }

                throw new System.ComponentModel.Win32Exception(
                    error,
                    "Credential Manager enumeration failed.");
            }

            try
            {
                for (int index = 0; index < count; index++)
                {
                    IntPtr pointer = Marshal.ReadIntPtr(credentials, index * IntPtr.Size);
                    NativeCredential credential = (NativeCredential)Marshal.PtrToStructure(
                        pointer,
                        typeof(NativeCredential));
                    if (credential.Type != CredentialType.Generic
                        || string.IsNullOrEmpty(credential.TargetName))
                    {
                        continue;
                    }

                    if (!CredDelete(credential.TargetName, CredentialType.Generic, 0))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorNotFound)
                        {
                            throw new System.ComponentModel.Win32Exception(
                                error,
                                "Credential Manager delete failed.");
                        }
                    }
                }
            }
            finally
            {
                CredFree(credentials);
            }
        }

        private static string QuoteArgument(string argument)
        {
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }

        private static void ShowLegacyUninstallMessage(
            LegacyUninstallMessage message,
            Exception exception,
            uint type)
        {
            LegacyUninstallText text = LegacyUninstallText.ForLanguage(
                ResolveLegacyUninstallLanguage());
            string body = message == LegacyUninstallMessage.NotInstalled
                ? text.NotInstalled
                : text.InstallerStartFailed;
            if (exception != null)
            {
                body += "\r\n\r\n" + text.ErrorCode + ": "
                    + FormatExceptionErrorCode(exception);
            }

            MessageBox(
                IntPtr.Zero,
                body,
                text.Title,
                type);
        }

        private static int ResolveLegacyUninstallLanguage()
        {
            int savedLanguage;
            if (TryReadSavedInstallerLanguage(out savedLanguage))
            {
                return SupportedLanguageOrEnglish(savedLanguage);
            }

            return MatchWindowsUiLanguage(GetUserDefaultUILanguage());
        }

        private static bool TryReadSavedInstallerLanguage(out int language)
        {
            language = EnglishLanguage;
            try
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                using (RegistryKey installer = machine.OpenSubKey(
                    InstallerRegistryPath,
                    writable: false))
                {
                    object value = installer == null
                        ? null
                        : installer.GetValue(
                            InstallerLanguageValueName,
                            null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null)
                    {
                        return false;
                    }

                    int parsedLanguage;
                    if (int.TryParse(
                        Convert.ToString(value, CultureInfo.InvariantCulture),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out parsedLanguage))
                    {
                        language = parsedLanguage;
                    }
                    return true;
                }
            }
            catch
            {
                // Missing or inaccessible installer state falls back to the
                // desktop user's Windows UI language.
                return false;
            }
        }

        private static int MatchWindowsUiLanguage(int windowsLanguage)
        {
            switch (windowsLanguage & 0x3ff)
            {
                case 0x09:
                    return EnglishLanguage;
                case 0x11:
                    return JapaneseLanguage;
                case 0x12:
                    return KoreanLanguage;
                case 0x19:
                    return RussianLanguage;
                case 0x22:
                    return UkrainianLanguage;
                case 0x04:
                    return windowsLanguage == 0x0404 || windowsLanguage == 0x0c04
                        || windowsLanguage == 0x1404 || windowsLanguage == 0x7c04
                            ? TraditionalChineseLanguage
                            : SimplifiedChineseLanguage;
                default:
                    return EnglishLanguage;
            }
        }

        private static int SupportedLanguageOrEnglish(int language)
        {
            switch (language)
            {
                case EnglishLanguage:
                case JapaneseLanguage:
                case KoreanLanguage:
                case RussianLanguage:
                case UkrainianLanguage:
                case SimplifiedChineseLanguage:
                case TraditionalChineseLanguage:
                    return language;
                default:
                    return EnglishLanguage;
            }
        }

        private static string FormatExceptionErrorCode(Exception exception)
        {
            var win32Exception = exception as System.ComponentModel.Win32Exception;
            if (win32Exception != null && win32Exception.NativeErrorCode != 0)
            {
                int nativeError = win32Exception.NativeErrorCode;
                return nativeError.ToString(CultureInfo.InvariantCulture)
                    + " (0x" + unchecked((uint)nativeError).ToString("X8", CultureInfo.InvariantCulture)
                    + ")";
            }

            return "0x" + unchecked((uint)exception.HResult).ToString(
                "X8",
                CultureInfo.InvariantCulture);
        }

        private enum LegacyUninstallMessage
        {
            NotInstalled,
            InstallerStartFailed,
        }

        private sealed class LegacyUninstallText
        {
            public readonly string Title;
            public readonly string NotInstalled;
            public readonly string InstallerStartFailed;
            public readonly string ErrorCode;

            private LegacyUninstallText(
                string title,
                string notInstalled,
                string installerStartFailed,
                string errorCode)
            {
                Title = title;
                NotInstalled = notInstalled;
                InstallerStartFailed = installerStartFailed;
                ErrorCode = errorCode;
            }

            public static LegacyUninstallText ForLanguage(int language)
            {
                switch (language)
                {
                    case KoreanLanguage:
                        return new LegacyUninstallText(
                            "SIDEY 제거",
                            "SIDEY가 설치되어 있지 않습니다.",
                            "Windows Installer를 시작하지 못했습니다.",
                            "오류 코드");
                    case JapaneseLanguage:
                        return new LegacyUninstallText(
                            "SIDEY のアンインストール",
                            "SIDEY はインストールされていません。",
                            "Windows Installer を起動できませんでした。",
                            "エラー コード");
                    case SimplifiedChineseLanguage:
                        return new LegacyUninstallText(
                            "卸载 SIDEY",
                            "未安装 SIDEY。",
                            "无法启动 Windows Installer。",
                            "错误代码");
                    case TraditionalChineseLanguage:
                        return new LegacyUninstallText(
                            "解除安裝 SIDEY",
                            "尚未安裝 SIDEY。",
                            "無法啟動 Windows Installer。",
                            "錯誤碼");
                    case RussianLanguage:
                        return new LegacyUninstallText(
                            "Удаление SIDEY",
                            "SIDEY не установлен.",
                            "Не удалось запустить установщик Windows.",
                            "Код ошибки");
                    case UkrainianLanguage:
                        return new LegacyUninstallText(
                            "Видалення SIDEY",
                            "SIDEY не встановлено.",
                            "Не вдалося запустити інсталятор Windows.",
                            "Код помилки");
                    default:
                        return new LegacyUninstallText(
                            "Uninstall SIDEY",
                            "SIDEY is not installed.",
                            "SIDEY could not start Windows Installer.",
                            "Error code");
                }
            }
        }

        private static class DesktopUserProcess
        {
            private const uint CreateUnicodeEnvironment = 0x00000400;
            private const uint Infinite = 0xFFFFFFFF;
            private const uint WaitFailed = 0xFFFFFFFF;
            private const uint LogonWithProfile = 0x00000001;
            private const uint ProcessQueryLimitedInformation = 0x1000;
            private const uint SePrivilegeEnabled = 0x00000002;
            private const uint TokenAdjustDefault = 0x0080;
            private const uint TokenAdjustPrivileges = 0x0020;
            private const uint TokenAdjustSessionId = 0x0100;
            private const uint TokenAssignPrimary = 0x0001;
            private const uint TokenDuplicate = 0x0002;
            private const uint TokenQuery = 0x0008;
            private const int SecurityImpersonation = 2;
            private const int TokenElevation = 20;
            private const int TokenPrimary = 1;

            public static int Start(
                string executable,
                string arguments,
                string workingDirectory,
                bool waitForExit)
            {
                bool elevated;
                int errorCode;
                if (!TryIsCurrentProcessElevated(out elevated, out errorCode))
                {
                    return errorCode;
                }
                if (!elevated)
                {
                    bool isCurrentDesktopUser;
                    if (!TryIsCurrentDesktopUser(out isCurrentDesktopUser, out errorCode))
                    {
                        return errorCode;
                    }
                    return isCurrentDesktopUser
                        ? StartNormally(executable, arguments, workingDirectory, waitForExit)
                        : ErrorAccessDenied;
                }

                IntPtr shellWindow = GetShellWindow();
                if (shellWindow == IntPtr.Zero)
                {
                    return HelperErrorDesktopUnavailable;
                }
                uint shellProcessId;
                if (GetWindowThreadProcessId(shellWindow, out shellProcessId) == 0)
                {
                    return GetLastWin32ErrorCode();
                }
                if (shellProcessId == 0)
                {
                    return HelperErrorDesktopUnavailable;
                }

                IntPtr shellProcess = IntPtr.Zero;
                IntPtr shellToken = IntPtr.Zero;
                IntPtr primaryToken = IntPtr.Zero;
                IntPtr environment = IntPtr.Zero;
                IntPtr currentToken = IntPtr.Zero;
                TokenPrivileges previousPrivileges = new TokenPrivileges();
                bool privilegeChanged = false;
                ProcessInformation processInformation = new ProcessInformation();
                try
                {
                    shellProcess = OpenProcess(
                        ProcessQueryLimitedInformation,
                        false,
                        shellProcessId);
                    if (shellProcess == IntPtr.Zero)
                    {
                        return GetLastWin32ErrorCode();
                    }
                    if (!OpenProcessToken(
                        shellProcess,
                        TokenQuery | TokenDuplicate,
                        out shellToken))
                    {
                        return GetLastWin32ErrorCode();
                    }

                    privilegeChanged = EnableImpersonatePrivilege(
                        out currentToken,
                        out previousPrivileges,
                        out errorCode);
                    if (!privilegeChanged)
                    {
                        return errorCode;
                    }

                    if (!DuplicateTokenEx(
                        shellToken,
                        TokenQuery | TokenAssignPrimary | TokenDuplicate
                            | TokenAdjustDefault | TokenAdjustSessionId,
                        IntPtr.Zero,
                        SecurityImpersonation,
                        TokenPrimary,
                        out primaryToken))
                    {
                        return GetLastWin32ErrorCode();
                    }
                    if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                    {
                        return GetLastWin32ErrorCode();
                    }

                    var commandLine = new StringBuilder(QuoteArgument(executable));
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        commandLine.Append(' ').Append(arguments);
                    }
                    var startupInformation = new StartupInformation
                    {
                        Size = Marshal.SizeOf(typeof(StartupInformation)),
                    };
                    if (!CreateProcessWithTokenW(
                        primaryToken,
                        LogonWithProfile,
                        executable,
                        commandLine,
                        CreateUnicodeEnvironment,
                        environment,
                        workingDirectory,
                        ref startupInformation,
                        out processInformation))
                    {
                        return GetLastWin32ErrorCode();
                    }

                    if (!waitForExit)
                    {
                        return 0;
                    }
                    if (WaitForSingleObject(processInformation.Process, Infinite) == WaitFailed)
                    {
                        return GetLastWin32ErrorCode();
                    }
                    int exitCode;
                    if (!GetExitCodeProcess(processInformation.Process, out exitCode))
                    {
                        return GetLastWin32ErrorCode();
                    }
                    return exitCode;
                }
                finally
                {
                    if (processInformation.Thread != IntPtr.Zero)
                        CloseHandle(processInformation.Thread);
                    if (processInformation.Process != IntPtr.Zero)
                        CloseHandle(processInformation.Process);
                    if (environment != IntPtr.Zero)
                        DestroyEnvironmentBlock(environment);
                    if (primaryToken != IntPtr.Zero)
                        CloseHandle(primaryToken);
                    if (shellToken != IntPtr.Zero)
                        CloseHandle(shellToken);
                    if (shellProcess != IntPtr.Zero)
                        CloseHandle(shellProcess);
                    if (privilegeChanged && currentToken != IntPtr.Zero)
                    {
                        TokenPrivileges ignored;
                        uint ignoredLength;
                        AdjustTokenPrivileges(
                            currentToken,
                            false,
                            ref previousPrivileges,
                            Marshal.SizeOf(typeof(TokenPrivileges)),
                            out ignored,
                            out ignoredLength);
                    }
                    if (currentToken != IntPtr.Zero)
                        CloseHandle(currentToken);
                }
            }

            private static int StartNormally(
                string executable,
                string arguments,
                string workingDirectory,
                bool waitForExit)
            {
                var start = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        return HelperErrorProcessNotStarted;
                    }
                    if (!waitForExit)
                    {
                        return 0;
                    }
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }

            public static bool IsCurrentDesktopUser()
            {
                bool isCurrentDesktopUser;
                int ignoredErrorCode;
                return TryIsCurrentDesktopUser(
                        out isCurrentDesktopUser,
                        out ignoredErrorCode)
                    && isCurrentDesktopUser;
            }

            private static bool TryIsCurrentDesktopUser(
                out bool isCurrentDesktopUser,
                out int errorCode)
            {
                isCurrentDesktopUser = false;
                errorCode = 0;
                IntPtr shellWindow = GetShellWindow();
                if (shellWindow == IntPtr.Zero)
                {
                    errorCode = HelperErrorDesktopUnavailable;
                    return false;
                }
                uint shellProcessId;
                if (GetWindowThreadProcessId(shellWindow, out shellProcessId) == 0)
                {
                    errorCode = GetLastWin32ErrorCode();
                    return false;
                }
                if (shellProcessId == 0)
                {
                    errorCode = HelperErrorDesktopUnavailable;
                    return false;
                }

                IntPtr shellProcess = IntPtr.Zero;
                IntPtr shellToken = IntPtr.Zero;
                try
                {
                    shellProcess = OpenProcess(
                        ProcessQueryLimitedInformation,
                        false,
                        shellProcessId);
                    if (shellProcess == IntPtr.Zero)
                    {
                        errorCode = GetLastWin32ErrorCode();
                        return false;
                    }
                    if (!OpenProcessToken(shellProcess, TokenQuery, out shellToken))
                    {
                        errorCode = GetLastWin32ErrorCode();
                        return false;
                    }

                    using (WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent())
                    using (var shellIdentity = new WindowsIdentity(shellToken))
                    {
                        isCurrentDesktopUser = currentIdentity.User != null
                            && shellIdentity.User != null
                            && currentIdentity.User.Equals(shellIdentity.User);
                        return true;
                    }
                }
                catch (Exception exception)
                {
                    errorCode = GetDesktopUserRunnerErrorCode(exception);
                    return false;
                }
                finally
                {
                    if (shellToken != IntPtr.Zero)
                        CloseHandle(shellToken);
                    if (shellProcess != IntPtr.Zero)
                        CloseHandle(shellProcess);
                }
            }

            private static bool TryIsCurrentProcessElevated(
                out bool elevated,
                out int errorCode)
            {
                elevated = false;
                errorCode = 0;
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                {
                    errorCode = GetLastWin32ErrorCode();
                    return false;
                }
                try
                {
                    int elevation;
                    uint returnedLength;
                    if (!GetTokenInformation(
                        token,
                        TokenElevation,
                        out elevation,
                        sizeof(int),
                        out returnedLength))
                    {
                        errorCode = GetLastWin32ErrorCode();
                        return false;
                    }
                    elevated = elevation != 0;
                    return true;
                }
                finally
                {
                    CloseHandle(token);
                }
            }

            private static bool EnableImpersonatePrivilege(
                out IntPtr token,
                out TokenPrivileges previous,
                out int errorCode)
            {
                previous = new TokenPrivileges();
                token = IntPtr.Zero;
                errorCode = 0;
                if (!OpenProcessToken(
                    GetCurrentProcess(),
                    TokenQuery | TokenAdjustPrivileges,
                    out token))
                {
                    errorCode = GetLastWin32ErrorCode();
                    return false;
                }

                Luid luid;
                if (!LookupPrivilegeValue(null, "SeImpersonatePrivilege", out luid))
                {
                    errorCode = GetLastWin32ErrorCode();
                    return false;
                }
                var requested = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Privileges = new LuidAndAttributes
                    {
                        Luid = luid,
                        Attributes = SePrivilegeEnabled,
                    },
                };
                uint returnedLength;
                if (!AdjustTokenPrivileges(
                        token,
                        false,
                        ref requested,
                        Marshal.SizeOf(typeof(TokenPrivileges)),
                        out previous,
                        out returnedLength))
                {
                    errorCode = GetLastWin32ErrorCode();
                    return false;
                }

                int privilegeError = Marshal.GetLastWin32Error();
                if (privilegeError != 0)
                {
                    errorCode = privilegeError;
                    return false;
                }

                return true;
            }

            private static int GetLastWin32ErrorCode()
            {
                int errorCode = Marshal.GetLastWin32Error();
                return errorCode != 0 ? errorCode : HelperErrorNativeCodeUnavailable;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct Luid
            {
                public uint LowPart;
                public int HighPart;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct LuidAndAttributes
            {
                public Luid Luid;
                public uint Attributes;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct TokenPrivileges
            {
                public uint PrivilegeCount;
                public LuidAndAttributes Privileges;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct StartupInformation
            {
                public int Size;
                public string Reserved;
                public string Desktop;
                public string Title;
                public uint X;
                public uint Y;
                public uint XSize;
                public uint YSize;
                public uint XCountChars;
                public uint YCountChars;
                public uint FillAttribute;
                public uint Flags;
                public short ShowWindow;
                public short Reserved2;
                public IntPtr Reserved2Pointer;
                public IntPtr StandardInput;
                public IntPtr StandardOutput;
                public IntPtr StandardError;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ProcessInformation
            {
                public IntPtr Process;
                public IntPtr Thread;
                public uint ProcessId;
                public uint ThreadId;
            }

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool AdjustTokenPrivileges(
                IntPtr token,
                bool disableAllPrivileges,
                ref TokenPrivileges newState,
                int bufferLength,
                out TokenPrivileges previousState,
                out uint returnLength);

            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);

            [DllImport("userenv.dll", SetLastError = true)]
            private static extern bool CreateEnvironmentBlock(
                out IntPtr environment,
                IntPtr token,
                bool inherit);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool CreateProcessWithTokenW(
                IntPtr token,
                uint logonFlags,
                string applicationName,
                StringBuilder commandLine,
                uint creationFlags,
                IntPtr environment,
                string currentDirectory,
                ref StartupInformation startupInformation,
                out ProcessInformation processInformation);

            [DllImport("userenv.dll", SetLastError = true)]
            private static extern bool DestroyEnvironmentBlock(IntPtr environment);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool DuplicateTokenEx(
                IntPtr existingToken,
                uint desiredAccess,
                IntPtr tokenAttributes,
                int impersonationLevel,
                int tokenType,
                out IntPtr newToken);

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

            [DllImport("user32.dll")]
            private static extern IntPtr GetShellWindow();

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool GetTokenInformation(
                IntPtr token,
                int informationClass,
                out int information,
                int informationLength,
                out uint returnLength);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern uint GetWindowThreadProcessId(
                IntPtr window,
                out uint processId);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool LookupPrivilegeValue(
                string systemName,
                string name,
                out Luid luid);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr OpenProcess(
                uint desiredAccess,
                bool inheritHandle,
                uint processId);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool OpenProcessToken(
                IntPtr process,
                uint desiredAccess,
                out IntPtr token);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        }

        private enum CredentialType : uint
        {
            Generic = 1,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public CredentialType Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiEnumRelatedProducts(
            string upgradeCode,
            uint reserved,
            uint productIndex,
            StringBuilder productCode);

        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredEnumerate(
            string filter,
            uint flags,
            out int count,
            out IntPtr credentials);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, CredentialType type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(
            IntPtr window,
            string text,
            string caption,
            uint type);
    }
}
