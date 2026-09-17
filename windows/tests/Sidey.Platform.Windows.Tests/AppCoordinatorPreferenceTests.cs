using Sidey.App.Services;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Platform.Windows;

namespace Sidey.Platform.Windows.Tests;

public sealed class AppCoordinatorPreferenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupMirrorSaveFailureDoesNotPreventCachedStateLoading(bool registered)
    {
        var store = new SaveFailingPreferences(
            AppPreferences.Default with { StartAtLogin = !registered });
        var startup = new FakeStartupService(registered);
        await using var coordinator = new AppCoordinator(store, startupService: startup);

        await coordinator.LoadCachedStateAsync();

        Assert.Equal(registered, coordinator.State.Preferences.StartAtLogin);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(registered ? 1 : 0, startup.UpgradeAttempts);
    }

    private sealed class FakeStartupService(bool enabled) : IWindowsStartupService
    {
        public int UpgradeAttempts { get; private set; }

        public bool IsEnabled() => enabled;

        public void SetEnabled(bool value) => enabled = value;

        public void UpgradeEnabledRegistration() => UpgradeAttempts++;
    }

    private sealed class SaveFailingPreferences(AppPreferences preferences) : IPreferencesStore
    {
        public int SaveAttempts { get; private set; }

        public ValueTask<AppPreferences> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(preferences);
        }

        public ValueTask SaveAsync(
            AppPreferences preferencesToSave,
            CancellationToken cancellationToken = default)
        {
            _ = preferencesToSave;
            cancellationToken.ThrowIfCancellationRequested();
            SaveAttempts++;
            throw new IOException("Synthetic preferences failure.");
        }
    }
}
