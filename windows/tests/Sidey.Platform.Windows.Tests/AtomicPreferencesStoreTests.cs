using Sidey.Core.Domain;
using Sidey.Infrastructure;

namespace Sidey.Platform.Windows.Tests;

public sealed class AtomicPreferencesStoreTests
{
    [Fact]
    public async Task PreviousSchemaLoadsWithEmptyProfileCache()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sidey-preferences-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "preferences.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "onboardingCompleted": true,
              "installationSeed": 1234,
              "overlayVisible": true,
              "quietMode": false,
              "showOfflineMembers": true,
              "startAtLogin": false,
              "activeRoomId": null,
              "overlayRegion": { "edge": "bottom", "span": "full", "monitorIdentifier": null }
            }
            """);

        try
        {
            AppPreferences preferences = await new AtomicPreferencesStore(path).LoadAsync();

            Assert.Equal(AppPreferences.CurrentSchemaVersion, preferences.SchemaVersion);
            Assert.Null(preferences.CachedNickname);
            Assert.Null(preferences.CachedCharacterId);
            Assert.False(preferences.RequiresRightClickToThrow);
            Assert.False(preferences.TreeMovementPaused);
            Assert.Null(preferences.Language);
            Assert.True(preferences.CharacterSoundEffectsEnabled);
            Assert.Equal(100, preferences.CharacterSoundEffectsVolume);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RoundTripPreservesActiveRoomAndWindowsSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sidey-preferences-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "preferences.json");
        var activeRoomId = Guid.NewGuid();
        AppPreferences expected = AppPreferences.CreateDefault(1234) with
        {
            OnboardingCompleted = true,
            OverlayVisible = false,
            QuietMode = true,
            CharacterSoundEffectsEnabled = false,
            CharacterSoundEffectsVolume = 0,
            ShowOfflineMembers = false,
            RequiresRightClickToThrow = true,
            TreeMovementPaused = true,
            StartAtLogin = true,
            Language = "ja-JP",
            Theme = AppThemePreference.Dark,
            CachedNickname = "윈도우 테스트",
            CachedCharacterId = "pixel_penguin",
            ActiveRoomId = activeRoomId,
            OverlayRegion = new OverlayRegionPreference(OverlayEdge.Left, OverlaySpan.Half, "monitor-2"),
        };

        try
        {
            var store = new AtomicPreferencesStore(path);
            await store.SaveAsync(expected);

            AppPreferences actual = await store.LoadAsync();

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
