using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Sidey.Installer
{
    internal static class InstallTransactionProgram
    {
        private const int InvalidArguments = 64;
        private const int RetryableCleanupFailure = 10;

        [STAThread]
        private static int Main(string[] arguments)
        {
            try
            {
                TransactionOptions options = TransactionOptions.Parse(arguments);
                return new InstallTransaction(options).Run();
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return InvalidArguments;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        private sealed class TransactionOptions
        {
            public string Action;
            public string InstallDirectory;
            public string StagingDirectory;
            public string RollbackDirectory;
            public string Version;
            public bool AllowUserWritableParentForTests;

            public static TransactionOptions Parse(string[] arguments)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool allowTests = false;
                for (int index = 0; index < arguments.Length; index++)
                {
                    string name = arguments[index];
                    if (string.Equals(
                        name,
                        "--allow-user-writable-parent-for-tests",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (allowTests)
                        {
                            throw new ArgumentException("A transaction option was specified more than once.");
                        }
                        allowTests = true;
                        continue;
                    }
                    if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
                    {
                        throw new ArgumentException("Invalid SIDEY install transaction arguments.");
                    }
                    if (values.ContainsKey(name))
                    {
                        throw new ArgumentException("A transaction option was specified more than once.");
                    }
                    values.Add(name, arguments[++index]);
                }

                var result = new TransactionOptions
                {
                    Action = Required(values, "--action"),
                    InstallDirectory = Required(values, "--install-directory"),
                    StagingDirectory = Required(values, "--staging-directory"),
                    RollbackDirectory = Required(values, "--rollback-directory"),
                    Version = Required(values, "--version"),
                    AllowUserWritableParentForTests = allowTests,
                };
                if (values.Count != 5 || !InstallTransaction.IsKnownAction(result.Action))
                {
                    throw new ArgumentException("Invalid SIDEY install transaction arguments.");
                }
                return result;
            }

            private static string Required(Dictionary<string, string> values, string name)
            {
                string value;
                if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("Missing SIDEY install transaction option: " + name);
                }
                return value;
            }
        }

        private sealed class PreviousRegistration
        {
            public bool Managed;
            public bool Existed;
            public string Version = string.Empty;
            public string Language = string.Empty;
            public string Location = string.Empty;
        }

        private sealed class TransactionState
        {
            public int SchemaVersion;
            public string Phase;
            public string Version;
            public string InstallDirectory;
            public string StagingDirectory;
            public string RollbackDirectory;
            public bool PreviousInstallExisted;
            public PreviousRegistration PreviousRegistration;
        }

        private sealed class InstallTransaction
        {
            // A stopped framework-dependent host or a security scanner can keep
            // the previous Runtime tree busy briefly. Keep replacement bounded.
            private const int ActivationMoveAttempts = 20;
            private const int ActivationMoveRetryDelayMilliseconds = 250;
            private const string TransactionRegistryPath = @"Software\SIDEY\InstallerTransaction";
            private const string InstallerRegistryPath = @"Software\SIDEY\Installer";
            private const string UninstallRegistryPath =
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SIDEY";
            private const string ProtocolRegistryPath = @"Software\Classes\sidey";
            private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

            private readonly string action;
            private readonly string installPath;
            private readonly string stagingPath;
            private readonly string rollbackPath;
            private readonly string version;
            private readonly string parentPath;
            private readonly string expectedStagingPrefix;
            private readonly string statePath;
            private readonly bool allowUserWritableParentForTests;
            private PreviousRegistration previousRegistration;
            private bool previousInstallExisted;
            private string transactionStagingPath;

            public InstallTransaction(TransactionOptions options)
            {
                action = options.Action;
                installPath = Normalize(options.InstallDirectory);
                stagingPath = Normalize(options.StagingDirectory);
                rollbackPath = Normalize(options.RollbackDirectory);
                version = options.Version;
                allowUserWritableParentForTests = options.AllowUserWritableParentForTests;

                DirectoryInfo parent = Directory.GetParent(installPath);
                string root = Path.GetPathRoot(installPath);
                if (parent == null || PathComparer.Equals(installPath, Normalize(root)))
                {
                    throw new InvalidOperationException(
                        "The SIDEY install directory cannot be a drive root.");
                }
                parentPath = Normalize(parent.FullName);
                expectedStagingPrefix = installPath + ".sidey-staging-";
                statePath = installPath + ".sidey-transaction.json";
                previousRegistration = null;
                previousInstallExisted = false;
                transactionStagingPath = stagingPath;

                ValidateSiblingPath(stagingPath, expectedStagingPrefix, false,
                    "The SIDEY staging directory must be a reserved sibling of the install directory.");
                ValidateSiblingPath(rollbackPath, installPath + ".sidey-rollback", true,
                    "The SIDEY rollback directory must be the reserved sibling of the install directory.");
            }

            public static bool IsKnownAction(string value)
            {
                return string.Equals(value, "Recover", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Prepare", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Activate", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "BeginRegistration", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Commit", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Rollback", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Complete", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "CleanupForUninstall", StringComparison.OrdinalIgnoreCase);
            }

            public int Run()
            {
                if (EqualsAction("Recover"))
                {
                    AssertSecureTransactionParent();
                    RecoverInterruptedTransaction();
                    ClearPendingInstallLocation();
                    return 0;
                }
                if (EqualsAction("Prepare"))
                {
                    AssertSecureTransactionParent();
                    RecoverInterruptedTransaction();
                    transactionStagingPath = stagingPath;
                    previousInstallExisted = Directory.Exists(installPath);
                    previousRegistration = GetPreviousRegistration();
                    SetPendingInstallLocation();
                    WriteState("staging");
                    Directory.CreateDirectory(stagingPath);
                    ProtectStagingDirectory();
                    return 0;
                }
                if (EqualsAction("Activate"))
                {
                    Activate();
                    return 0;
                }
                if (EqualsAction("BeginRegistration"))
                {
                    TransactionState state = ReadState();
                    if (state == null || !PhaseEquals(state, "active")
                        || !File.Exists(Path.Combine(installPath, "SIDEY.exe")))
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install transaction is not ready to register.");
                    }
                    WriteState("registering");
                    return 0;
                }
                if (EqualsAction("Commit"))
                {
                    TransactionState state = ReadState();
                    if (state == null || !PhaseEquals(state, "registering")
                        || !File.Exists(Path.Combine(installPath, "SIDEY.exe")))
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install transaction is not ready to commit.");
                    }
                    WriteState("committed");
                    return 0;
                }
                if (EqualsAction("Rollback"))
                {
                    TransactionState state = ReadState();
                    if (state != null)
                    {
                        UndoTransaction(state);
                    }
                    else
                    {
                        RemoveTransactionDirectory(stagingPath);
                        ClearPendingInstallLocation();
                    }
                    return 0;
                }
                if (EqualsAction("Complete"))
                {
                    TransactionState state = ReadState();
                    if (state == null || !PhaseEquals(state, "committed"))
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install transaction was not committed.");
                    }
                    try
                    {
                        RemoveTransactionDirectory(rollbackPath);
                        RemoveTransactionDirectory(AssertStagingDirectoryPath(state.StagingDirectory));
                        RemoveState();
                        ClearPendingInstallLocation();
                        return 0;
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine(exception.Message);
                        return RetryableCleanupFailure;
                    }
                }

                AssertSecureTransactionParent();
                CleanupForUninstall();
                return 0;
            }

            private bool EqualsAction(string expected)
            {
                return string.Equals(action, expected, StringComparison.OrdinalIgnoreCase);
            }

            private static bool PhaseEquals(TransactionState state, string expected)
            {
                return string.Equals(state.Phase, expected, StringComparison.Ordinal);
            }

            private void Activate()
            {
                TransactionState state = ReadState();
                if (state == null || !PhaseEquals(state, "staging")
                    || !PathComparer.Equals(Normalize(state.StagingDirectory), stagingPath))
                {
                    throw new InvalidOperationException(
                        "The SIDEY staging transaction is not ready to activate.");
                }

                string[] requiredPaths =
                {
                    Path.Combine(stagingPath, "SIDEY.exe"),
                    Path.Combine(stagingPath, @"Runtime\SIDEY.Host.exe"),
                    Path.Combine(stagingPath, @"Runtime\SIDEY.UninstallHelper.exe"),
                    Path.Combine(stagingPath, "Uninstall.exe"),
                };
                foreach (string requiredPath in requiredPaths)
                {
                    if (!File.Exists(requiredPath))
                    {
                        throw new InvalidOperationException(
                            "The staged SIDEY payload is incomplete: " + requiredPath);
                    }
                }

                AssertNoReparseTree(stagingPath);
                AssertOrdinaryDirectory(installPath);
                if (Directory.Exists(rollbackPath))
                {
                    throw new InvalidOperationException(
                        "The SIDEY rollback directory was not cleared before activation.");
                }

                WriteState("prepared");
                try
                {
                    if (Directory.Exists(installPath))
                    {
                        MoveDirectoryForActivation(installPath, rollbackPath);
                        WriteState("previous-moved");
                    }
                    WriteState("activating");
                    MoveDirectoryForActivation(stagingPath, installPath);
                    WriteState("active");
                }
                catch
                {
                    TransactionState failedState = ReadState();
                    if (failedState != null)
                    {
                        UndoTransaction(failedState);
                    }
                    throw;
                }
            }

            private static void MoveDirectoryForActivation(string source, string destination)
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        Directory.Move(source, destination);
                        return;
                    }
                    catch (Exception exception) when (
                        IsRetryableMoveFailure(exception)
                        && attempt < ActivationMoveAttempts)
                    {
                        Thread.Sleep(ActivationMoveRetryDelayMilliseconds);
                    }
                }
            }

            private static bool IsRetryableMoveFailure(Exception exception)
            {
                return exception is IOException || exception is UnauthorizedAccessException;
            }

            private void CleanupForUninstall()
            {
                TransactionState state = ReadState();
                if (state != null)
                {
                    if (PhaseEquals(state, "committed"))
                    {
                        RemoveTransactionDirectory(rollbackPath);
                        RemoveTransactionDirectory(AssertStagingDirectoryPath(state.StagingDirectory));
                        RemoveState();
                        ClearPendingInstallLocation();
                    }
                    else
                    {
                        UndoTransaction(state);
                    }
                }
                else
                {
                    RemoveTransactionDirectory(rollbackPath);
                    RemoveTransactionDirectory(stagingPath);
                    ClearPendingInstallLocation();
                }
            }

            private void RecoverInterruptedTransaction()
            {
                TransactionState state = ReadState();
                if (state == null)
                {
                    if (Directory.Exists(rollbackPath))
                    {
                        throw new InvalidOperationException(
                            "An unrecognized SIDEY rollback directory already exists.");
                    }
                    RemoveTransactionDirectory(stagingPath);
                    return;
                }

                if (PhaseEquals(state, "committed"))
                {
                    string recordedStaging = AssertStagingDirectoryPath(state.StagingDirectory);
                    if (!Directory.Exists(installPath))
                    {
                        UndoTransaction(state);
                        return;
                    }
                    RemoveTransactionDirectory(rollbackPath);
                    RemoveTransactionDirectory(recordedStaging);
                    RemoveState();
                    return;
                }
                UndoTransaction(state);
            }

            private void UndoTransaction(TransactionState state)
            {
                string stagingToRemove = AssertStagingDirectoryPath(state.StagingDirectory);
                string startingPhase = state.Phase;
                if (string.Equals(startingPhase, "rolled-back", StringComparison.Ordinal))
                {
                    RemoveTransactionDirectory(rollbackPath);
                    RemoveTransactionDirectory(stagingToRemove);
                    if (!PathComparer.Equals(stagingToRemove, stagingPath))
                    {
                        RemoveTransactionDirectory(stagingPath);
                    }
                    RemoveState();
                    ClearPendingInstallLocation();
                    return;
                }

                if (!string.Equals(startingPhase, "rolling-back", StringComparison.Ordinal))
                {
                    WriteState("rolling-back");
                }
                AssertOrdinaryDirectory(rollbackPath);
                if (Directory.Exists(rollbackPath))
                {
                    if (Directory.Exists(installPath))
                    {
                        RemoveTransactionDirectory(installPath);
                    }
                    Directory.Move(rollbackPath, installPath);
                }
                else if (previousInstallExisted
                    && !string.Equals(startingPhase, "staging", StringComparison.Ordinal)
                    && !string.Equals(startingPhase, "prepared", StringComparison.Ordinal)
                    && !string.Equals(startingPhase, "rolling-back", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The SIDEY rollback directory is missing for the previous installation.");
                }
                else if (!previousInstallExisted
                    && (string.Equals(startingPhase, "activating", StringComparison.Ordinal)
                        || string.Equals(startingPhase, "active", StringComparison.Ordinal)
                        || string.Equals(startingPhase, "registering", StringComparison.Ordinal)
                        || string.Equals(startingPhase, "committed", StringComparison.Ordinal)
                        || string.Equals(startingPhase, "rolling-back", StringComparison.Ordinal)))
                {
                    RemoveTransactionDirectory(installPath);
                }

                RemoveTransactionDirectory(stagingToRemove);
                if (!PathComparer.Equals(stagingToRemove, stagingPath))
                {
                    RemoveTransactionDirectory(stagingPath);
                }
                if (string.Equals(startingPhase, "registering", StringComparison.Ordinal)
                    || string.Equals(startingPhase, "committed", StringComparison.Ordinal)
                    || string.Equals(startingPhase, "rolling-back", StringComparison.Ordinal))
                {
                    RestorePreviousRegistration();
                }
                WriteState("rolled-back");
                RemoveState();
                ClearPendingInstallLocation();
            }

            private void ValidateSiblingPath(
                string path,
                string expected,
                bool exact,
                string message)
            {
                DirectoryInfo parent = Directory.GetParent(path);
                bool nameMatches = exact
                    ? PathComparer.Equals(path, expected)
                    : path.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
                if (!nameMatches || parent == null
                    || !PathComparer.Equals(Normalize(parent.FullName), parentPath))
                {
                    throw new InvalidOperationException(message);
                }
            }

            private string AssertStagingDirectoryPath(string path)
            {
                string normalized = Normalize(path);
                ValidateSiblingPath(
                    normalized,
                    expectedStagingPrefix,
                    false,
                    "The SIDEY transaction state contains an unsafe staging directory.");
                return normalized;
            }

            private static string Normalize(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("A SIDEY transaction path is empty.");
                }
                return Path.GetFullPath(path).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }

            private static void AssertOrdinaryDirectory(string path)
            {
                if (Directory.Exists(path)
                    && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Refusing to use a reparse point for the SIDEY install transaction: " + path);
                }
            }

            private static void AssertNoReparseAncestors(string path)
            {
                for (DirectoryInfo current = new DirectoryInfo(path);
                    current != null;
                    current = current.Parent)
                {
                    if (current.Exists
                        && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            "Refusing a reparse-point ancestor for the SIDEY install transaction: "
                            + current.FullName);
                    }
                }
            }

            private static void AssertNoReparseTree(string path)
            {
                if (!Directory.Exists(path))
                {
                    return;
                }
                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(path));
                while (pending.Count > 0)
                {
                    DirectoryInfo directory = pending.Pop();
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            "Refusing a reparse point in the SIDEY install transaction: "
                            + directory.FullName);
                    }
                    foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
                    {
                        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidOperationException(
                                "Refusing a reparse point in the SIDEY install transaction: "
                                + item.FullName);
                        }
                        DirectoryInfo child = item as DirectoryInfo;
                        if (child != null)
                        {
                            pending.Push(child);
                        }
                    }
                }
            }

            private void AssertSecureTransactionParent()
            {
                AssertNoReparseAncestors(parentPath);
                if (allowUserWritableParentForTests)
                {
                    return;
                }

                const string administrators = "S-1-5-32-544";
                const string system = "S-1-5-18";
                const string trustedInstaller =
                    "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
                const string creatorOwner = "S-1-3-0";
                var privileged = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    administrators,
                    system,
                    trustedInstaller,
                };

                DirectorySecurity security = Directory.GetAccessControl(
                    parentPath,
                    AccessControlSections.Access | AccessControlSections.Owner);
                SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier))
                    as SecurityIdentifier;
                if (owner == null || !privileged.Contains(owner.Value))
                {
                    throw new InvalidOperationException(
                        "The SIDEY install directory parent must be owned by Administrators, SYSTEM, or TrustedInstaller.");
                }

                const FileSystemRights writeRights = FileSystemRights.Write
                    | FileSystemRights.Delete
                    | FileSystemRights.DeleteSubdirectoriesAndFiles
                    | FileSystemRights.ChangePermissions
                    | FileSystemRights.TakeOwnership;
                AuthorizationRuleCollection rules = security.GetAccessRules(
                    true,
                    true,
                    typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    uint rawRights = unchecked((uint)rule.FileSystemRights);
                    bool hasGenericWrite = (rawRights & 0x50000000U) != 0;
                    string identity = rule.IdentityReference.Value;
                    bool safeCreatorOwnerInheritance = string.Equals(
                            identity,
                            creatorOwner,
                            StringComparison.OrdinalIgnoreCase)
                        && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0;
                    if (rule.AccessControlType == AccessControlType.Allow
                        && !safeCreatorOwnerInheritance
                        && (((rule.FileSystemRights & writeRights) != 0) || hasGenericWrite)
                        && !privileged.Contains(identity))
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install directory parent is writable by an unprivileged identity: "
                            + identity);
                    }
                }
            }

            private void ProtectStagingDirectory()
            {
                if (allowUserWritableParentForTests)
                {
                    return;
                }
                var administrators = new SecurityIdentifier("S-1-5-32-544");
                var system = new SecurityIdentifier("S-1-5-18");
                var users = new SecurityIdentifier("S-1-5-32-545");
                const InheritanceFlags inheritance =
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.SetOwner(administrators);
                security.AddAccessRule(new FileSystemAccessRule(
                    administrators,
                    FileSystemRights.FullControl,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    system,
                    FileSystemRights.FullControl,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    users,
                    FileSystemRights.ReadAndExecute,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                Directory.SetAccessControl(stagingPath, security);
            }

            private void RemoveTransactionDirectory(string path)
            {
                if (!Directory.Exists(path))
                {
                    return;
                }
                AssertNoReparseTree(path);
                RemoveDirectoryTree(new DirectoryInfo(path));
            }

            private static void RemoveDirectoryTree(DirectoryInfo directory)
            {
                directory.Refresh();
                if (!directory.Exists)
                {
                    return;
                }
                AssertNotReparsePoint(directory);
                foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
                {
                    item.Refresh();
                    AssertNotReparsePoint(item);
                    DirectoryInfo childDirectory = item as DirectoryInfo;
                    if (childDirectory != null)
                    {
                        RemoveDirectoryTree(childDirectory);
                        continue;
                    }

                    NormalizeDeletionAttributes(item);
                    File.Delete(item.FullName);
                }

                // Recheck immediately before deletion. Directory.Delete(false)
                // never follows a newly substituted directory tree recursively.
                directory.Refresh();
                AssertNotReparsePoint(directory);
                NormalizeDeletionAttributes(directory);
                Directory.Delete(directory.FullName, false);
            }

            private static void AssertNotReparsePoint(FileSystemInfo item)
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Refusing a reparse point in the SIDEY install transaction: "
                        + item.FullName);
                }
            }

            private static void NormalizeDeletionAttributes(FileSystemInfo item)
            {
                FileAttributes attributes = item.Attributes;
                FileAttributes normalized = attributes
                    & ~FileAttributes.ReadOnly
                    & ~FileAttributes.Hidden
                    & ~FileAttributes.System;
                if (normalized != attributes)
                {
                    File.SetAttributes(item.FullName, normalized);
                }
            }

            private void SetPendingInstallLocation()
            {
                if (allowUserWritableParentForTests)
                {
                    return;
                }
                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                using (RegistryKey key = machine.CreateSubKey(TransactionRegistryPath))
                {
                    key.SetValue("InstallLocation", installPath, RegistryValueKind.String);
                }
            }

            private void ClearPendingInstallLocation()
            {
                if (allowUserWritableParentForTests)
                {
                    return;
                }
                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                {
                    machine.DeleteSubKeyTree(TransactionRegistryPath, false);
                }
            }

            private PreviousRegistration GetPreviousRegistration()
            {
                if (allowUserWritableParentForTests)
                {
                    return new PreviousRegistration { Managed = false };
                }
                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                using (RegistryKey key = machine.OpenSubKey(InstallerRegistryPath))
                {
                    object previousVersion = key == null
                        ? null
                        : key.GetValue("InstalledVersion", null);
                    return new PreviousRegistration
                    {
                        Managed = true,
                        Existed = previousVersion != null,
                        Version = Convert.ToString(previousVersion, CultureInfo.InvariantCulture),
                        Language = key == null
                            ? string.Empty
                            : Convert.ToString(key.GetValue("Language", string.Empty), CultureInfo.InvariantCulture),
                        Location = key == null
                            ? installPath
                            : Convert.ToString(key.GetValue("InstallLocation", installPath), CultureInfo.InvariantCulture),
                    };
                }
            }

            private void RestorePreviousRegistration()
            {
                if (previousRegistration == null || !previousRegistration.Managed)
                {
                    return;
                }

                using (RegistryKey machine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64))
                {
                    machine.DeleteSubKeyTree(InstallerRegistryPath, false);
                    machine.DeleteSubKeyTree(UninstallRegistryPath, false);
                    machine.DeleteSubKeyTree(ProtocolRegistryPath, false);

                    string startMenu = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                        "SIDEY");
                    RemoveTransactionDirectory(startMenu);
                    if (!previousRegistration.Existed)
                    {
                        return;
                    }

                    string location = previousRegistration.Location;
                    string priorVersion = previousRegistration.Version;
                    using (RegistryKey installer = machine.CreateSubKey(InstallerRegistryPath))
                    using (RegistryKey uninstall = machine.CreateSubKey(UninstallRegistryPath))
                    using (RegistryKey protocol = machine.CreateSubKey(ProtocolRegistryPath))
                    {
                        if (!string.IsNullOrWhiteSpace(previousRegistration.Language))
                        {
                            installer.SetValue(
                                "Language",
                                previousRegistration.Language,
                                RegistryValueKind.String);
                        }
                        installer.SetValue("InstallLocation", location, RegistryValueKind.String);
                        installer.SetValue("InstalledVersion", priorVersion, RegistryValueKind.String);
                        uninstall.SetValue("DisplayName", "SIDEY", RegistryValueKind.String);
                        uninstall.SetValue("Publisher", "SIDEY", RegistryValueKind.String);
                        uninstall.SetValue("InstallLocation", location, RegistryValueKind.String);
                        uninstall.SetValue(
                            "DisplayIcon",
                            Path.Combine(location, @"Assets\Icons\SideyAppIcon.ico"),
                            RegistryValueKind.String);
                        uninstall.SetValue(
                            "UninstallString",
                            Quote(Path.Combine(location, "Uninstall.exe")),
                            RegistryValueKind.String);
                        uninstall.SetValue(
                            "QuietUninstallString",
                            Quote(Path.Combine(location, "Uninstall.exe")) + " /S",
                            RegistryValueKind.String);
                        uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        uninstall.SetValue("DisplayVersion", priorVersion, RegistryValueKind.String);
                        protocol.SetValue(
                            string.Empty,
                            "URL:SIDEY authentication callback",
                            RegistryValueKind.String);
                        protocol.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
                        using (RegistryKey icon = protocol.CreateSubKey("DefaultIcon"))
                        using (RegistryKey command = protocol.CreateSubKey(@"shell\open\command"))
                        {
                            icon.SetValue(
                                string.Empty,
                                Path.Combine(location, @"Assets\Icons\SideyAppIcon.ico"),
                                RegistryValueKind.String);
                            command.SetValue(
                                string.Empty,
                                Quote(Path.Combine(location, "SIDEY.exe")) + " \"%1\"",
                                RegistryValueKind.String);
                        }
                    }

                    Directory.CreateDirectory(startMenu);
                    CreateShortcut(
                        Path.Combine(startMenu, "SIDEY.lnk"),
                        Path.Combine(location, "SIDEY.exe"),
                        Path.Combine(location, @"Assets\Icons\SideyAppIcon.ico"));
                    CreateShortcut(
                        Path.Combine(startMenu, "Uninstall SIDEY.lnk"),
                        Path.Combine(location, "Uninstall.exe"),
                        Path.Combine(location, @"Assets\Icons\SideyAppIcon.ico"));
                }
            }

            private static string Quote(string value)
            {
                return "\"" + value + "\"";
            }

            private static void CreateShortcut(string path, string target, string icon)
            {
                var shellLink = (IShellLinkW)new ShellLink();
                try
                {
                    shellLink.SetPath(target);
                    shellLink.SetIconLocation(icon, 0);
                    ((IPersistFile)shellLink).Save(path, true);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(shellLink);
                }
            }

            private void WriteState(string phase)
            {
                string temporaryPath = statePath + ".tmp";
                AssertOrdinaryStateFile(statePath);
                AssertOrdinaryStateFile(temporaryPath);
                var state = new TransactionState
                {
                    SchemaVersion = 1,
                    Phase = phase,
                    Version = version,
                    InstallDirectory = installPath,
                    StagingDirectory = transactionStagingPath,
                    RollbackDirectory = rollbackPath,
                    PreviousInstallExisted = previousInstallExisted,
                    PreviousRegistration = previousRegistration,
                };
                File.WriteAllText(temporaryPath, StateJson.Serialize(state), new UTF8Encoding(false));
                if (!MoveFileEx(
                    temporaryPath,
                    statePath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not publish SIDEY transaction state.");
                }
            }

            private TransactionState ReadState()
            {
                if (!File.Exists(statePath))
                {
                    return null;
                }
                AssertOrdinaryStateFile(statePath);
                TransactionState state = StateJson.Deserialize(
                    File.ReadAllText(statePath, Encoding.UTF8));
                if (state.SchemaVersion != 1
                    || !PathComparer.Equals(Normalize(state.InstallDirectory), installPath)
                    || !PathComparer.Equals(Normalize(state.RollbackDirectory), rollbackPath))
                {
                    throw new InvalidOperationException(
                        "The SIDEY install transaction state is invalid.");
                }
                transactionStagingPath = AssertStagingDirectoryPath(state.StagingDirectory);
                previousRegistration = state.PreviousRegistration;
                previousInstallExisted = state.PreviousInstallExisted;
                return state;
            }

            private void RemoveState()
            {
                foreach (string path in new[] { statePath, statePath + ".tmp" })
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    AssertOrdinaryStateFile(path);
                    File.Delete(path);
                }
            }

            private static void AssertOrdinaryStateFile(string path)
            {
                if (File.Exists(path)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Refusing a reparse point for SIDEY transaction state.");
                }
            }

            private const uint MoveFileReplaceExisting = 0x1;
            private const uint MoveFileWriteThrough = 0x8;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool MoveFileEx(
                string existingFile,
                string newFile,
                uint flags);
        }

        private static class StateJson
        {
            public static string Serialize(TransactionState state)
            {
                var builder = new StringBuilder();
                builder.Append('{');
                Property(builder, "schemaVersion", state.SchemaVersion.ToString(CultureInfo.InvariantCulture), false);
                Property(builder, "phase", String(state.Phase), true);
                Property(builder, "version", String(state.Version), true);
                Property(builder, "installDirectory", String(state.InstallDirectory), true);
                Property(builder, "stagingDirectory", String(state.StagingDirectory), true);
                Property(builder, "rollbackDirectory", String(state.RollbackDirectory), true);
                Property(builder, "previousInstallExisted", state.PreviousInstallExisted ? "true" : "false", true);
                builder.Append(',').Append(String("previousRegistration")).Append(':');
                if (state.PreviousRegistration == null)
                {
                    builder.Append("null");
                }
                else
                {
                    PreviousRegistration registration = state.PreviousRegistration;
                    builder.Append('{');
                    Property(builder, "managed", registration.Managed ? "true" : "false", false);
                    Property(builder, "existed", registration.Existed ? "true" : "false", true);
                    Property(builder, "version", String(registration.Version), true);
                    Property(builder, "language", String(registration.Language), true);
                    Property(builder, "location", String(registration.Location), true);
                    builder.Append('}');
                }
                return builder.Append('}').ToString();
            }

            public static TransactionState Deserialize(string json)
            {
                object parsed = new JsonParser(json).Parse();
                IDictionary<string, object> root = parsed as IDictionary<string, object>;
                if (root == null)
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                var state = new TransactionState
                {
                    SchemaVersion = Integer(root, "schemaVersion"),
                    Phase = Text(root, "phase"),
                    Version = Text(root, "version"),
                    InstallDirectory = Text(root, "installDirectory"),
                    StagingDirectory = Text(root, "stagingDirectory"),
                    RollbackDirectory = Text(root, "rollbackDirectory"),
                    PreviousInstallExisted = Boolean(root, "previousInstallExisted", false),
                };
                object registrationValue;
                if (root.TryGetValue("previousRegistration", out registrationValue)
                    && registrationValue != null)
                {
                    IDictionary<string, object> registration =
                        registrationValue as IDictionary<string, object>;
                    if (registration == null)
                    {
                        throw new InvalidOperationException(
                            "The SIDEY install transaction state is invalid.");
                    }
                    state.PreviousRegistration = new PreviousRegistration
                    {
                        Managed = Boolean(registration, "managed", false),
                        Existed = Boolean(registration, "existed", false),
                        Version = OptionalText(registration, "version"),
                        Language = OptionalText(registration, "language"),
                        Location = OptionalText(registration, "location"),
                    };
                }
                return state;
            }

            private static void Property(
                StringBuilder builder,
                string name,
                string value,
                bool comma)
            {
                if (comma)
                {
                    builder.Append(',');
                }
                builder.Append(String(name)).Append(':').Append(value);
            }

            private static string String(string value)
            {
                if (value == null)
                {
                    return "null";
                }
                var builder = new StringBuilder(value.Length + 2).Append('"');
                foreach (char character in value)
                {
                    switch (character)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (character < 0x20)
                            {
                                builder.Append("\\u")
                                    .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(character);
                            }
                            break;
                    }
                }
                return builder.Append('"').ToString();
            }

            private static int Integer(IDictionary<string, object> values, string key)
            {
                object value;
                if (!values.TryGetValue(key, out value) || !(value is long))
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                return checked((int)(long)value);
            }

            private static string Text(IDictionary<string, object> values, string key)
            {
                object value;
                string text;
                if (!values.TryGetValue(key, out value)
                    || (text = value as string) == null
                    || string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                return text;
            }

            private static string OptionalText(IDictionary<string, object> values, string key)
            {
                object value;
                if (!values.TryGetValue(key, out value) || value == null)
                {
                    return string.Empty;
                }
                string text = value as string;
                if (text == null)
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                return text;
            }

            private static bool Boolean(
                IDictionary<string, object> values,
                string key,
                bool defaultValue)
            {
                object value;
                if (!values.TryGetValue(key, out value))
                {
                    return defaultValue;
                }
                if (!(value is bool))
                {
                    throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
                }
                return (bool)value;
            }
        }

        private sealed class JsonParser
        {
            private readonly string text;
            private int index;

            public JsonParser(string textValue)
            {
                text = textValue ?? string.Empty;
            }

            public object Parse()
            {
                object value = Value();
                WhiteSpace();
                if (index != text.Length)
                {
                    Invalid();
                }
                return value;
            }

            private object Value()
            {
                WhiteSpace();
                if (index >= text.Length)
                {
                    Invalid();
                }
                char character = text[index];
                if (character == '{') return Object();
                if (character == '[') return Array();
                if (character == '"') return String();
                if (character == '-' || char.IsDigit(character)) return Number();
                if (Literal("true")) return true;
                if (Literal("false")) return false;
                if (Literal("null")) return null;
                Invalid();
                return null;
            }

            private IDictionary<string, object> Object()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                index++;
                WhiteSpace();
                if (Take('}')) return result;
                while (true)
                {
                    WhiteSpace();
                    if (index >= text.Length || text[index] != '"') Invalid();
                    string key = String();
                    WhiteSpace();
                    if (!Take(':')) Invalid();
                    if (result.ContainsKey(key)) Invalid();
                    result.Add(key, Value());
                    WhiteSpace();
                    if (Take('}')) return result;
                    if (!Take(',')) Invalid();
                }
            }

            private IList Array()
            {
                var result = new ArrayList();
                index++;
                WhiteSpace();
                if (Take(']')) return result;
                while (true)
                {
                    result.Add(Value());
                    WhiteSpace();
                    if (Take(']')) return result;
                    if (!Take(',')) Invalid();
                }
            }

            private string String()
            {
                index++;
                var result = new StringBuilder();
                while (index < text.Length)
                {
                    char character = text[index++];
                    if (character == '"') return result.ToString();
                    if (character != '\\')
                    {
                        if (character < 0x20) Invalid();
                        result.Append(character);
                        continue;
                    }
                    if (index >= text.Length) Invalid();
                    char escape = text[index++];
                    switch (escape)
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            if (index + 4 > text.Length) Invalid();
                            int code;
                            if (!int.TryParse(
                                text.Substring(index, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out code)) Invalid();
                            result.Append((char)code);
                            index += 4;
                            break;
                        default: Invalid(); break;
                    }
                }
                Invalid();
                return null;
            }

            private long Number()
            {
                int start = index;
                if (text[index] == '-') index++;
                if (index >= text.Length || !char.IsDigit(text[index])) Invalid();
                while (index < text.Length && char.IsDigit(text[index])) index++;
                long value;
                if (!long.TryParse(
                    text.Substring(start, index - start),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value)) Invalid();
                return value;
            }

            private bool Literal(string value)
            {
                if (index + value.Length > text.Length
                    || !string.Equals(
                        text.Substring(index, value.Length),
                        value,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                index += value.Length;
                return true;
            }

            private bool Take(char character)
            {
                if (index < text.Length && text[index] == character)
                {
                    index++;
                    return true;
                }
                return false;
            }

            private void WhiteSpace()
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            }

            private static void Invalid()
            {
                throw new InvalidOperationException("The SIDEY install transaction state is invalid.");
            }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximumPath,
                IntPtr findData, uint flags);
            void GetIDList(out IntPtr itemIdentifierList);
            void SetIDList(IntPtr itemIdentifierList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximumName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
                int maximumPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
                int iconPathLength,
                out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
