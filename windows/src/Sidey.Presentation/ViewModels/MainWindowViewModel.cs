using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Presentation.Services;

namespace Sidey.Presentation.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly string[] s_supportedLanguages = [.. I18n.SupportedLanguages];

    private readonly IMainWindowCoordinator _coordinator;
    private readonly IMainWindowDialogService _dialogs;
    private readonly IUpdateService _updates;
    private readonly HashSet<Guid> _expandedRoomIds = [];
    private readonly HashSet<CommerceProductKind> _pendingCosmeticKinds = [];
    private readonly Dictionary<CommerceProductKind, string?> _pendingCosmeticIds = [];
    private string? _pendingCharacterId;
    private long _selectionGeneration;
    private Guid? _selectionUserId;
    private (bool Enabled, int Volume)? _pendingSoundSettings;
    private bool _savingSoundSettings;
    private int _lastNonzeroSoundVolume = 100;
    private CancellationTokenSource? _soundSaveDelay;
    private Task _saveSoundSettingsTask = Task.CompletedTask;
    private readonly Guid _soundFeedbackScope = Guid.NewGuid();
    private bool _soundFeedbackPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundMuteActionText))]
    public partial bool CharacterSoundEffectsEnabled { get; set; } = true;
    public string SoundMuteActionText => I18n.Get(CharacterSoundEffectsEnabled ? "settings.muteSound" : "settings.unmuteSound");

    [RelayCommand]
    private void ToggleCharacterSoundMute() => CharacterSoundEffectsEnabled = !CharacterSoundEffectsEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CharacterSoundVolumeLabel))]
    public partial double CharacterSoundEffectsVolume { get; set; } = 100;
    public string CharacterSoundVolumeLabel => $"{CharacterSoundEffectsVolume:0}%";

    partial void OnCharacterSoundEffectsVolumeChanged(double value)
    {
        if (_isApplyingState)
            return;
        int volume = double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), 0, 100) : 100;
        if (volume > 0)
            _lastNonzeroSoundVolume = volume;
        _isApplyingState = true;
        try
        { CharacterSoundEffectsEnabled = volume > 0; }
        finally { _isApplyingState = false; }
        QueueSoundSettings(volume > 0, volume);
        if (volume == 0)
            StopSoundVolumeFeedback();
        else
            _soundFeedbackPending = true;
    }

    partial void OnCharacterSoundEffectsEnabledChanged(bool value)
    {
        if (_isApplyingState)
            return;
        StopSoundVolumeFeedback();
        int volume = (int)CharacterSoundEffectsVolume;
        if (value && volume == 0)
        {
            volume = _lastNonzeroSoundVolume;
            _isApplyingState = true;
            try
            { CharacterSoundEffectsVolume = volume; }
            finally { _isApplyingState = false; }
        }
        QueueSoundSettings(value, volume);
    }

    private void QueueSoundSettings(bool enabled, int volume)
    {
        _coordinator.ApplyCharacterSoundEffects(enabled, volume);
        _pendingSoundSettings = (enabled, volume);
        if (!_savingSoundSettings)
            _saveSoundSettingsTask = SaveSoundSettingsAsync();
    }

    public void CompleteSoundVolumeAdjustment()
    {
        if (!_soundFeedbackPending)
            return;
        _soundFeedbackPending = false;
        if (!CharacterSoundEffectsEnabled || CharacterSoundEffectsVolume <= 0)
            return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _coordinator.StopImpactSounds(_soundFeedbackScope);
        _coordinator.PlayImpactSound("patch_soft_ball", _soundFeedbackScope, now);
    }

    public void StopSoundVolumeFeedback()
    {
        _soundFeedbackPending = false;
        _coordinator.StopImpactSounds(_soundFeedbackScope);
    }

    public Task FlushSoundSettingsAsync()
    {
        _soundSaveDelay?.Cancel();
        return _saveSoundSettingsTask;
    }

    private async Task SaveSoundSettingsAsync()
    {
        _savingSoundSettings = true;
        try
        {
            while (_pendingSoundSettings is { } settings)
            {
                using var delay = new CancellationTokenSource();
                _soundSaveDelay = delay;
                try
                { await Task.Delay(200, delay.Token); }
                catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
                finally { _soundSaveDelay = null; }
                if (_pendingSoundSettings != settings && !delay.IsCancellationRequested)
                    continue;
                settings = _pendingSoundSettings ?? settings;
                _pendingSoundSettings = null;
                bool saved = await RunCommandAsync(
                    () => _coordinator.SaveCharacterSoundEffectsAsync(settings.Enabled, settings.Volume), null,
                    () => _pendingSoundSettings is null);
                if (!saved && _pendingSoundSettings is null)
                {
                    StopSoundVolumeFeedback();
                    AppPreferences previous = _coordinator.State.Preferences;
                    _isApplyingState = true;
                    try
                    {
                        CharacterSoundEffectsEnabled = previous.CharacterSoundEffectsEnabled;
                        CharacterSoundEffectsVolume = previous.CharacterSoundEffectsVolume;
                    }
                    finally { _isApplyingState = false; }
                    _coordinator.ApplyCharacterSoundEffects(previous.CharacterSoundEffectsEnabled, previous.CharacterSoundEffectsVolume);
                }
            }
        }
        finally { _savingSoundSettings = false; }
    }

    public void RefreshFeedbackPresentation()
    {
        foreach (CharacterSelectionItemViewModel item in CharacterSelections)
        {
            item.AnimationsEnabled = _coordinator.AnimationsEnabled;
            item.RefreshSelectionStatus();
        }
        foreach (CosmeticSelectionItemViewModel? item in BubbleSelections.Concat(ThrowableSelections))
        {
            item.AnimationsEnabled = _coordinator.AnimationsEnabled;
            item.RefreshSelectionStatus();
        }
    }
    private CoordinatorState _state = CoordinatorState.Initial;
    private CoordinatorState _previousState = CoordinatorState.Initial;
    private bool _isApplyingState;
    private bool _hasAppliedState;
    private bool _roomExpansionInitialized;
    private string _syncedProfileNickname = string.Empty;
    private string _syncedProfileCharacterId = PixelCharacterCatalog.FallbackId;
    private AvailableUpdate? _lastAvailableUpdate;
    private string? _updateActivityKey;
    private object?[] _updateActivityArguments = [];

    [ObservableProperty]
    public partial string Nickname { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedCharacterId { get; set; } = PixelCharacterCatalog.FallbackId;

    [ObservableProperty]
    public partial string CreateRoomName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InviteCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CreateRoomActionText { get; set; } = I18n.Get("groups.create");

    [ObservableProperty]
    public partial string JoinRoomActionText { get; set; } = I18n.Get("groups.joinByCode");

    [ObservableProperty]
    public partial bool AreGroupMutationsEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    public partial bool IsSavingProfile { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    public partial bool IsSavingCharacter { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    public partial bool HasNicknameChanges { get; set; }

    [ObservableProperty]
    public partial int SelectedStoreKindIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedStoreSortIndex { get; set; }

    [ObservableProperty]
    public partial bool HidesOwnedStoreProducts { get; set; }

    [ObservableProperty]
    public partial string StoreSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasVisibleStoreProducts { get; set; }

    [ObservableProperty]
    public partial bool IsRemoteContentLoading { get; set; }

    [ObservableProperty]
    public partial bool IsCharacterSelectionsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsBubbleSelectionsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsThrowableSelectionsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsRoomsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsStoreLoading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryConnectionCommand))]
    public partial bool IsConnected { get; set; }

    private bool CanRetryConnection() => !IsConnected;

    [RelayCommand(CanExecute = nameof(CanRetryConnection))]
    private async Task RetryConnectionAsync()
    {
        await RunCommandAsync(() => _coordinator.RetryConnectionAsync(), successMessage: null);
    }

    [ObservableProperty]
    public partial string ConnectionText { get; set; } = I18n.Get("connection.reconnecting");

    [ObservableProperty]
    public partial bool HasRooms { get; set; }

    [ObservableProperty]
    public partial bool IsOverlayVisible { get; set; }

    [ObservableProperty]
    public partial bool IsQuietMode { get; set; }

    [ObservableProperty]
    public partial bool ShowOfflineMembers { get; set; }

    [ObservableProperty]
    public partial bool RequiresRightClickToThrow { get; set; }

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    [ObservableProperty]
    public partial bool IsLanguageSelectionEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool IsThemeSelectionEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedEdgeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedSpanIndex { get; set; } = 2;

    [ObservableProperty]
    public partial string? SelectedMonitorIdentifier { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    public partial bool IsCheckingForUpdates { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateActivity))]
    public partial string UpdateActivityText { get; set; } = string.Empty;

    public bool HasUpdateActivity => !string.IsNullOrWhiteSpace(UpdateActivityText);

    [ObservableProperty]
    public partial string CurrentVersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastUpdateCheckText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ValidationPathText { get; set; } = I18n.Get("metrics.rendererNotStarted");

    [ObservableProperty]
    public partial string ValidationMetricsText { get; set; } = I18n.Get("metrics.noSamples");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportDiagnosticDataCommand))]
    public partial bool IsExportingDiagnosticData { get; set; }

    public MainWindowViewModel(
        IMainWindowCoordinator coordinator,
        IMainWindowDialogService dialogs,
        IUpdateService updates)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        IsRemoteContentLoading = coordinator.IsRemoteContentLoading;
        StoreProducts = [.. WindowsCommerceCatalog.Products.Select(CreateStorePreview)];
        foreach (StoreProductPreviewViewModel product in StoreProducts.Where(product => product.Kind == CommerceProductKind.Character))
        {
            string? keepsakeId = WindowsCommerceCatalog.KeepsakeFor(product.CharacterId)?.Id;
            product.RelatedKeepsake = StoreProducts.FirstOrDefault(candidate => candidate.ProductId == keepsakeId);
        }
        RefreshVisibleStoreProducts();
        RefreshMonitors();
        ApplyState(coordinator.State);
        UpdateCharacterSelectionState();
        RefreshUpdateInformation();
    }

    public event Action<NoticeMessage>? NoticeRaised;
    public event Action<StoreProductPreviewViewModel>? StorePreviewRequested;

    public ObservableCollection<CharacterSelectionItemViewModel> CharacterSelections { get; } = [];

    public ObservableCollection<CosmeticSelectionItemViewModel> BubbleSelections { get; } = [];

    public ObservableCollection<CosmeticSelectionItemViewModel> ThrowableSelections { get; } = [];

    public IReadOnlyList<StoreProductPreviewViewModel> StoreProducts { get; }

    [ObservableProperty]
    public partial IReadOnlyList<StoreProductPreviewViewModel> VisibleStoreProducts { get; set; } = [];

    public ObservableCollection<MonitorOption> Monitors { get; } = [];

    public ObservableCollection<RoomCardViewModel> Rooms { get; } = [];

    public bool IsValidationMode => _coordinator.IsValidationMode;

    private StoreProductPreviewViewModel CreateStorePreview(CommerceProduct product)
    {
        PixelCharacterDefinition character = PixelCharacterCatalog.Get(product.CharacterId);
        string displayName = product.Kind == CommerceProductKind.Character
            ? character.DisplayName
            : I18n.Get($"store.product.{product.Id}");
        string description = I18n.Get($"store.productDescriptions.{product.Id}");
        return new StoreProductPreviewViewModel(
            product,
            displayName,
            description,
            I18n.Format("store.priceKrw", product.AmountKrw),
            () => ActivateStoreProductAsync(product.Id),
            () => StorePreviewRequested?.Invoke(StoreProducts.FirstOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.ProductId, product.Id))!));
    }

    public void PrepareGroupsForPresentation()
    {
        _expandedRoomIds.Clear();
        if (_state.ActiveRoomId is { } activeRoomId)
        {
            _expandedRoomIds.Add(activeRoomId);
            int activeRoomIndex = IndexOfRoom(activeRoomId);
            if (activeRoomIndex > 0)
            {
                Rooms.Move(activeRoomIndex, 0);
            }
        }

        RefreshRoomCards();
    }

    public void ApplyState(CoordinatorState state)
    {
        IsRemoteContentLoading = _coordinator.IsRemoteContentLoading;
        IsCharacterSelectionsLoading = state.ContentLoading.Snapshot.NeedsSkeleton;
        IsBubbleSelectionsLoading = state.ContentLoading.Snapshot.NeedsSkeleton;
        IsThrowableSelectionsLoading = state.ContentLoading.Snapshot.NeedsSkeleton;
        IsRoomsLoading = state.ContentLoading.Snapshot.NeedsSkeleton;
        IsStoreLoading = state.ContentLoading.Store.NeedsSkeleton;
        if (_selectionUserId != state.Profile?.Id)
        {
            _selectionUserId = state.Profile?.Id;
            _selectionGeneration++;
            _pendingCharacterId = null;
            _pendingCosmeticIds.Clear();
            _pendingCosmeticKinds.Clear();
            IsSavingCharacter = false;
        }
        bool shouldApplyProfileDraft = NicknameDraftMatchesSyncedState();
        (string syncedNickname, string syncedCharacterId) = GetSyncedProfileDraft(state);
        _syncedProfileNickname = syncedNickname;
        _syncedProfileCharacterId = syncedCharacterId;

        _isApplyingState = true;
        try
        {
            _expandedRoomIds.IntersectWith(state.Rooms.Select(room => room.Id));
            if (!_roomExpansionInitialized || state.ActiveRoomId != _state.ActiveRoomId)
            {
                _expandedRoomIds.Clear();
                if (state.ActiveRoomId is { } activeRoomId)
                {
                    _expandedRoomIds.Add(activeRoomId);
                }

                _roomExpansionInitialized = true;
            }

            _state = state;
            RefreshCharacterSelections(state.ActiveEntitlementKeys);
            RefreshStoreProducts(state);
            if (shouldApplyProfileDraft)
            {
                Nickname = syncedNickname;
                SelectedCharacterId = syncedCharacterId;
            }
            RefreshCosmeticSelections(state);
            UpdateCosmeticSelectionAvailability(CommerceProductKind.Bubble, !_pendingCosmeticKinds.Contains(CommerceProductKind.Bubble));
            UpdateCosmeticSelectionAvailability(CommerceProductKind.Throwable, !_pendingCosmeticKinds.Contains(CommerceProductKind.Throwable));
            UpdateCharacterSelectionState();
            RefreshFeedbackPresentation();
            RefreshNicknameChangeState();

            RefreshRoomCards();
            HasRooms = state.Rooms.Count > 0;
            AreGroupMutationsEnabled = state.GroupOperation == GroupOperation.Idle;
            CreateRoomActionText = state.GroupOperation == GroupOperation.Creating
                ? I18n.Get("groups.creating")
                : I18n.Get("groups.create");
            JoinRoomActionText = state.GroupOperation == GroupOperation.Joining
                ? I18n.Get("groups.joining")
                : I18n.Get("groups.joinByCode");
            IsConnected = state.Connected;
            ConnectionText = state.Connected
                ? I18n.Get("connection.connected")
                : I18n.Get("connection.disconnected");
            IsOverlayVisible = state.Preferences.OverlayVisible;
            IsQuietMode = state.Preferences.QuietMode;
            if (!_savingSoundSettings)
            {
                CharacterSoundEffectsEnabled = state.Preferences.CharacterSoundEffectsEnabled;
                CharacterSoundEffectsVolume = state.Preferences.CharacterSoundEffectsVolume;
                if (CharacterSoundEffectsVolume > 0)
                    _lastNonzeroSoundVolume = (int)CharacterSoundEffectsVolume;
            }
            ShowOfflineMembers = state.Preferences.ShowOfflineMembers;
            RequiresRightClickToThrow = state.Preferences.RequiresRightClickToThrow;
            StartAtLogin = state.Preferences.StartAtLogin;
            string selectedLanguage = state.Preferences.Language ?? I18n.Language;
            int selectedLanguageIndex = Array.FindIndex(
                s_supportedLanguages,
                language => string.Equals(language, selectedLanguage, StringComparison.OrdinalIgnoreCase));
            SelectedLanguageIndex = Math.Max(0, selectedLanguageIndex);
            SelectedThemeIndex = (int)state.Preferences.Theme;
            SelectedEdgeIndex = (int)state.Preferences.OverlayRegion.Edge;
            SelectedSpanIndex = (int)state.Preferences.OverlayRegion.Span;
            string? preferredMonitor = state.Preferences.OverlayRegion.MonitorIdentifier;
            SelectedMonitorIdentifier = Monitors.Any(monitor =>
                    StringComparer.Ordinal.Equals(monitor.Identifier, preferredMonitor))
                ? preferredMonitor
                : Monitors.FirstOrDefault(monitor => monitor.IsPrimary)?.Identifier
                    ?? Monitors.FirstOrDefault()?.Identifier;
            RefreshValidationMetrics();
            RaiseConnectionNotice(state);
            _previousState = state;
            _hasAppliedState = true;
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SoundMuteActionText));
        RefreshFeedbackPresentation();
        // Keep the existing items, selection, drafts and in-flight commands alive.
        foreach (CharacterSelectionItemViewModel character in CharacterSelections)
            character.DisplayName = PixelCharacterCatalog.Get(character.Id).DisplayName;
        foreach (CosmeticSelectionItemViewModel? cosmetic in BubbleSelections.Concat(ThrowableSelections))
            cosmetic.DisplayName = I18n.Get(cosmetic.CatalogItemId is { } id
                ? $"store.product.{id}"
                : cosmetic.Kind == CommerceProductKind.Bubble ? "profile.defaultBubble" : "profile.defaultThrowable");
        foreach (StoreProductPreviewViewModel product in StoreProducts)
        {
            StoreProductPreviewViewModel localized = CreateStorePreview(WindowsCommerceCatalog.Products.First(item => item.Id == product.ProductId));
            product.DisplayName = localized.DisplayName;
            product.Description = localized.Description;
            product.FormattedPrice = localized.FormattedPrice;
        }
        ApplyState(_coordinator.State);
        RefreshUpdateInformation();
        SetUpdateActivity(_updateActivityKey, _updateActivityArguments);
    }

    private void SetUpdateActivity(string? key, params object?[] arguments)
    {
        _updateActivityKey = key;
        _updateActivityArguments = arguments;
        UpdateActivityText = key is null ? string.Empty : I18n.Format(key, arguments);
    }

    public void ReportError(Exception exception) =>
        RaiseNotice(exception.Message, NoticeKind.Error);

    public void ReportSuccess(string message) => RaiseNotice(message, NoticeKind.Success);

    public void RefreshDiagnostics() => RefreshValidationMetrics();

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (IsSavingProfile)
        {
            return;
        }

        IsSavingProfile = true;
        try
        {
            await RunCommandAsync(
                () => _coordinator.SaveProfileAsync(Nickname, SelectedCharacterId),
                I18n.Get("profile.saved"));
        }
        finally
        {
            IsSavingProfile = false;
        }
    }

    private bool CanSaveProfile() =>
        HasNicknameChanges
        && !IsSavingProfile
        && !IsSavingCharacter
        && ProfileValidator.IsValidNickname(Nickname);

    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        string submittedName = CreateRoomName;
        if (await RunCommandAsync(
            () => _coordinator.CreateRoomAsync(submittedName),
            I18n.Get("groups.createdSimple"))
            && StringComparer.Ordinal.Equals(CreateRoomName, submittedName))
        {
            CreateRoomName = string.Empty;
        }
    }

    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        string submittedCode = InviteCode;
        if (await RunCommandAsync(
            () => _coordinator.JoinRoomAsync(submittedCode),
            I18n.Get("groups.joinedSimple"))
            && StringComparer.Ordinal.Equals(InviteCode, submittedCode))
        {
            InviteCode = string.Empty;
        }
    }

    [RelayCommand]
    private void Compose() => _coordinator.RequestComposer();

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        SetUpdateActivity("update.checking");
        try
        {
            AvailableUpdate? update = await _updates.CheckAsync();
            _lastAvailableUpdate = update;
            if (update is null)
            {
                RaiseNotice(I18n.Get("update.latest"), NoticeKind.Success);
                return;
            }

            SetUpdateActivity(null);
            if (!await _dialogs.ConfirmUpdateDownloadAsync(update.Version))
            {
                return;
            }

            SetUpdateActivity("update.downloading");
            var downloadProgress = new Progress<int>(percentage =>
            {
                SetUpdateActivity(
                    "update.downloadingProgress",
                    percentage);
            });
            await _updates.DownloadAndLaunchInstallerAsync(
                update,
                progress: downloadProgress);
            RaiseNotice(
                I18n.Get("update.installerLaunched"),
                NoticeKind.Success);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            RaiseNotice(I18n.Get("update.installCancelled"), NoticeKind.Informational);
        }
        catch (Win32Exception)
        {
            RaiseNotice(I18n.Get("update.installerLaunchFailed"), NoticeKind.Error);
        }
        catch (Exception)
        {
            RaiseNotice(I18n.Get("update.failed"), NoticeKind.Error);
        }
        finally
        {
            RefreshUpdateInformation();
            SetUpdateActivity(null);
            IsCheckingForUpdates = false;
        }
    }

    public async Task<AvailableUpdate?> CheckForUpdatesOnStartupAsync()
    {
        if (IsCheckingForUpdates)
        {
            return null;
        }

        IsCheckingForUpdates = true;
        SetUpdateActivity("update.checking");
        try
        {
            AvailableUpdate? update = await _updates.CheckAsync();
            _lastAvailableUpdate = update;
            return update;
        }
        finally
        {
            RefreshUpdateInformation();
            SetUpdateActivity(null);
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task OpenReleaseNotesAsync()
    {
        try
        {
            Uri releaseNotesUri = _lastAvailableUpdate?.ReleaseNotesUri
                ?? _updates.CurrentReleaseNotesUri;
            await _updates.OpenReleaseNotesAsync(releaseNotesUri);
        }
        catch (Exception exception)
        {
            RaiseNotice(
                I18n.Format("update.releaseNotesFailed", exception.Message),
                NoticeKind.Error);
        }
    }

    private void RefreshUpdateInformation()
    {
        CurrentVersionText = $"v{_updates.CurrentVersion}";
        if (_updates.LastCheckedAt is not { } checkedAt)
        {
            LastUpdateCheckText = I18n.Get("settings.updateNeverChecked");
            return;
        }

        DateTimeOffset local = checkedAt.ToLocalTime();
        DateTime today = DateTime.Today;
        string display = local.Date == today
            ? I18n.Format(
                "settings.updateCheckedToday",
                local.ToString("t", CultureInfo.CurrentCulture))
            : local.Date == today.AddDays(-1)
                ? I18n.Format(
                    "settings.updateCheckedYesterday",
                    local.ToString("t", CultureInfo.CurrentCulture))
                : local.ToString("g", CultureInfo.CurrentCulture);
        LastUpdateCheckText = I18n.Format("settings.updateLastChecked", display);
    }

    [RelayCommand]
    private async Task ExportValidationMetricsAsync()
    {
        try
        {
            string? path = await _coordinator.ExportValidationMetricsAsync();
            if (path is null)
            {
                RaiseNotice(I18n.Get("metrics.rendererNotRunning"), NoticeKind.Warning);
                return;
            }

            ValidationPathText = I18n.Format("metrics.exportPath", path);
            RaiseNotice(
                I18n.Get("metrics.exported"),
                NoticeKind.Success);
        }
        catch (Exception exception)
        {
            RaiseNotice(I18n.Format("metrics.exportFailed", exception.Message), NoticeKind.Error);
        }
    }

    private bool CanExportDiagnosticData() => !IsExportingDiagnosticData;

    [RelayCommand(CanExecute = nameof(CanExportDiagnosticData))]
    private async Task ExportDiagnosticDataAsync()
    {
        IsExportingDiagnosticData = true;
        try
        {
            await _coordinator.ExportDiagnosticDataAsync();
            RaiseNotice(I18n.Get("about.diagnosticsExported"), NoticeKind.Success);
        }
        catch (Exception)
        {
            RaiseNotice(I18n.Get("about.diagnosticsExportFailed"), NoticeKind.Error);
        }
        finally
        {
            IsExportingDiagnosticData = false;
        }
    }

    [RelayCommand]
    private async Task OpenExternalLinkAsync(string url)
    {
        try
        {
            await _coordinator.OpenExternalUriAsync(new Uri(url, UriKind.Absolute));
        }
        catch (Exception)
        {
            RaiseNotice(I18n.Get("about.linkOpenFailed"), NoticeKind.Error);
        }
    }

    partial void OnIsOverlayVisibleChanged(bool value)
    {
        if (!_isApplyingState)
        {
            _ = RunCommandAsync(() => _coordinator.SetOverlayVisibleAsync(value), null);
        }
    }

    partial void OnIsQuietModeChanged(bool value)
    {
        if (!_isApplyingState)
        {
            _ = RunCommandAsync(() => _coordinator.SetQuietModeAsync(value), null);
        }
    }

    partial void OnShowOfflineMembersChanged(bool value)
    {
        if (!_isApplyingState)
        {
            _ = RunCommandAsync(() => _coordinator.SetShowOfflineMembersAsync(value), null);
        }
    }

    partial void OnRequiresRightClickToThrowChanged(bool value)
    {
        if (!_isApplyingState)
        {
            _ = RunCommandAsync(() => _coordinator.SetRequiresRightClickToThrowAsync(value), null);
        }
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (!_isApplyingState)
        {
            _ = RunCommandAsync(() => _coordinator.SetStartAtLoginAsync(value), null);
        }
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (!_isApplyingState && IsLanguageSelectionEnabled && value >= 0 && value < s_supportedLanguages.Length)
            _ = SaveLanguageAsync(value);
    }

    private async Task SaveLanguageAsync(int index)
    {
        IsLanguageSelectionEnabled = false;
        string language = s_supportedLanguages[index];
        await RunCommandAsync(() => _coordinator.SetLanguageAsync(language), null);
        ApplyState(_coordinator.State);
        IsLanguageSelectionEnabled = true;
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (!_isApplyingState && IsThemeSelectionEnabled && Enum.IsDefined((AppThemePreference)value))
            _ = SaveThemeAsync((AppThemePreference)value);
    }

    private async Task SaveThemeAsync(AppThemePreference theme)
    {
        IsThemeSelectionEnabled = false;
        await RunCommandAsync(() => _coordinator.SetThemeAsync(theme), null);
        ApplyState(_coordinator.State);
        IsThemeSelectionEnabled = true;
    }

    partial void OnNicknameChanged(string value)
    {
        _ = value;
        RefreshNicknameChangeState();
    }

    partial void OnSelectedCharacterIdChanged(string value)
    {
        // GridView can clear SelectedValue while its items are being replaced.
        // A cleared or unavailable selection is not a request to save the fallback.
        if (!_isApplyingState
            && (string.IsNullOrEmpty(value)
                || !CharacterSelections.Any(character => StringComparer.Ordinal.Equals(character.Id, value))))
        {
            _isApplyingState = true;
            try
            {
                SelectedCharacterId = _syncedProfileCharacterId;
            }
            finally
            {
                _isApplyingState = false;
            }
            return;
        }

        UpdateCharacterSelectionState();
        if (!_isApplyingState
            && !StringComparer.Ordinal.Equals(value, _syncedProfileCharacterId))
        {
            _ = SaveCharacterSelectionAsync(value);
        }
    }

    partial void OnSelectedStoreKindIndexChanged(int value)
    {
        _ = value;
        RefreshVisibleStoreProducts();
    }

    partial void OnSelectedStoreSortIndexChanged(int value)
    {
        _ = value;
        RefreshVisibleStoreProducts();
    }

    partial void OnHidesOwnedStoreProductsChanged(bool value)
    {
        _ = value;
        RefreshVisibleStoreProducts();
    }

    partial void OnStoreSearchTextChanged(string value)
    {
        _ = value;
        RefreshVisibleStoreProducts();
    }

    [RelayCommand]
    private void ResetStoreFilters()
    {
        SelectedStoreSortIndex = 0;
        HidesOwnedStoreProducts = false;
        StoreSearchText = string.Empty;
    }

    partial void OnSelectedEdgeIndexChanged(int value) => ApplyRegionPreference();

    partial void OnSelectedSpanIndexChanged(int value) => ApplyRegionPreference();

    partial void OnSelectedMonitorIdentifierChanged(string? value) => ApplyRegionPreference();

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    private async Task ActivateStoreProductAsync(string productId)
    {
        CommerceProductState? state = _coordinator.State.CommerceProducts.FirstOrDefault(product =>
            StringComparer.Ordinal.Equals(product.Product.Id, productId));
        string successMessage = state?.GoogleConnected == true
            ? I18n.Get("store.purchaseCompleted")
            : I18n.Get("store.googleConnectionOpened");
        await RunCommandAsync(
            () => _coordinator.ActivateStoreProductAsync(productId),
            successMessage);
    }

    private async Task SaveCharacterSelectionAsync(string characterId)
    {
        if (IsSavingCharacter)
        {
            return;
        }

        IsSavingCharacter = true;
        long generation = _selectionGeneration;
        _pendingCharacterId = characterId;
        UpdateCharacterSelectionState();
        bool succeeded = await RunCommandAsync(
            () => _coordinator.SaveProfileAsync(_syncedProfileNickname, characterId),
            I18n.Get("profile.characterSaved"), () => generation == _selectionGeneration);
        if (generation != _selectionGeneration)
            return;
        _pendingCharacterId = null;
        IsSavingCharacter = false;
        if (succeeded)
            _syncedProfileCharacterId = PixelCharacterCatalog.NormalizeId(characterId);
        UpdateCharacterSelectionState();
        if (succeeded)
        {
            return;
        }

        _isApplyingState = true;
        try
        {
            SelectedCharacterId = _syncedProfileCharacterId;
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void RefreshStoreProducts(CoordinatorState state)
    {
        var states = state.CommerceProducts
            .ToDictionary(item => item.Product.Id, StringComparer.Ordinal);
        foreach (StoreProductPreviewViewModel product in StoreProducts)
        {
            CommerceProductState productState = states.GetValueOrDefault(product.ProductId)
                ?? new CommerceProductState(
                    WindowsCommerceCatalog.Find(product.ProductId)!,
                    GoogleConnected: false,
                    CommercePurchaseState.Unavailable);
            product.Apply(
                productState,
                state.DevelopmentCommerceEnabled,
                state.ActiveEntitlementKeys.Contains(productState.Product.EntitlementKey)
                    || productState.PurchaseState == CommercePurchaseState.Owned);
        }
        RefreshVisibleStoreProducts();
    }

    private void RefreshVisibleStoreProducts()
    {
        if (StoreProducts is null)
        {
            return;
        }

        var kind = (CommerceProductKind)Math.Clamp(SelectedStoreKindIndex, 0, 2);
        IEnumerable<StoreProductPreviewViewModel> products = StoreProducts
            .Where(product => product.Kind == kind)
            .Where(product => !HidesOwnedStoreProducts || !product.IsOwned);
        string searchText = StoreSearchText.Trim();
        if (searchText.Length > 0)
        {
            products = products.Where(product =>
                product.DisplayName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || product.Description.Contains(searchText, StringComparison.CurrentCultureIgnoreCase));
        }
        products = SelectedStoreSortIndex switch
        {
            1 => products.OrderBy(product => product.AmountKrw)
                .ThenBy(product => product.SortOrder)
                .ThenBy(product => product.ProductId, StringComparer.Ordinal),
            2 => products.OrderByDescending(product => product.AmountKrw)
                .ThenBy(product => product.SortOrder)
                .ThenBy(product => product.ProductId, StringComparer.Ordinal),
            _ => products.OrderBy(product => product.SortOrder)
                .ThenBy(product => product.ProductId, StringComparer.Ordinal),
        };

        StoreProductPreviewViewModel[] desiredProducts = [.. products];
        if (!VisibleStoreProducts.SequenceEqual(desiredProducts))
        {
            VisibleStoreProducts = desiredProducts;
        }

        HasVisibleStoreProducts = desiredProducts.Length > 0;
    }

    private void RefreshNicknameChangeState()
    {
        HasNicknameChanges = !StringComparer.Ordinal.Equals(
            ProfileValidator.NormalizeNickname(Nickname),
            ProfileValidator.NormalizeNickname(_syncedProfileNickname));
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    private void UpdateCharacterSelectionState()
    {
        foreach (CharacterSelectionItemViewModel character in CharacterSelections)
        {
            character.IsSelected = StringComparer.Ordinal.Equals(character.Id, _syncedProfileCharacterId);
            character.IsPending = StringComparer.Ordinal.Equals(character.Id, _pendingCharacterId);
            character.IsEnabled = !IsSavingCharacter;
            character.AnimationsEnabled = _coordinator.AnimationsEnabled;
        }
    }

    private void RefreshCharacterSelections(IReadOnlySet<string> activeEntitlementKeys)
    {
        IReadOnlyList<PixelCharacterDefinition> desired =
            PixelCharacterCatalog.SelectableFor(activeEntitlementKeys);
        if (CharacterSelections.Select(character => character.Id)
            .SequenceEqual(desired.Select(character => character.Id), StringComparer.Ordinal))
        {
            return;
        }

        CharacterSelections.Clear();
        foreach (PixelCharacterDefinition character in desired)
        {
            CharacterSelections.Add(new CharacterSelectionItemViewModel(
                character.Id,
                character.DisplayName,
                character.Id));
        }
    }

    private void RefreshCosmeticSelections(CoordinatorState state)
    {
        RefreshCosmeticSelection(
            BubbleSelections,
            CommerceProductKind.Bubble,
            state.Profile?.EquippedBubbleStyleId,
            state.ActiveEntitlementKeys);
        RefreshCosmeticSelection(
            ThrowableSelections,
            CommerceProductKind.Throwable,
            state.Profile?.EquippedThrowableId,
            state.ActiveEntitlementKeys);
    }

    private void RefreshCosmeticSelection(
        ObservableCollection<CosmeticSelectionItemViewModel> destination,
        CommerceProductKind kind,
        string? selectedId,
        IReadOnlySet<string> activeEntitlementKeys)
    {
        CommerceProduct[] entitledProducts = [.. WindowsCommerceCatalog.Products.Where(product =>
                product.Kind == kind && activeEntitlementKeys.Contains(product.EntitlementKey))];
        string?[] desiredIds = [null, .. entitledProducts.Select(product => product.EffectiveCatalogItemId)];
        bool canReuseItems = destination.Count == desiredIds.Length
            && destination.Select(item => item.CatalogItemId).SequenceEqual(
                desiredIds,
                StringComparer.Ordinal);

        if (!canReuseItems)
        {
            destination.Clear();
            destination.Add(new CosmeticSelectionItemViewModel(
                kind,
                null,
                I18n.Get(kind == CommerceProductKind.Bubble
                    ? "profile.defaultBubble"
                    : "profile.defaultThrowable"),
                SelectedCharacterId,
                selectedId is null,
                !_pendingCosmeticKinds.Contains(kind),
                () => SetEquippedCosmeticAsync(kind, null)));
            foreach (CommerceProduct product in entitledProducts)
            {
                string id = product.EffectiveCatalogItemId;
                destination.Add(new CosmeticSelectionItemViewModel(
                    kind,
                    id,
                    I18n.Get($"store.product.{product.Id}"),
                    SelectedCharacterId,
                    StringComparer.Ordinal.Equals(id, selectedId),
                    !_pendingCosmeticKinds.Contains(kind),
                    () => SetEquippedCosmeticAsync(kind, id)));
            }
            return;
        }

        bool isEnabled = !_pendingCosmeticKinds.Contains(kind);
        foreach (CosmeticSelectionItemViewModel item in destination)
        {
            item.CharacterId = SelectedCharacterId;
            item.IsSelected = StringComparer.Ordinal.Equals(item.CatalogItemId, selectedId);
            item.IsEnabled = isEnabled;
        }
    }

    private async Task SetEquippedCosmeticAsync(CommerceProductKind kind, string? catalogItemId)
    {
        if (!_pendingCosmeticKinds.Add(kind))
        {
            return;
        }

        long generation = _selectionGeneration;
        _pendingCosmeticIds[kind] = catalogItemId;
        UpdateCosmeticSelectionAvailability(kind, false);
        try
        {
            await RunCommandAsync(
                () => _coordinator.SetEquippedCosmeticAsync(kind, catalogItemId),
                I18n.Get("profile.cosmeticSaved"), () => generation == _selectionGeneration);
        }
        finally
        {
            if (generation == _selectionGeneration)
            {
                _pendingCosmeticKinds.Remove(kind);
                _pendingCosmeticIds.Remove(kind);
                UpdateCosmeticSelectionAvailability(kind, true);
            }
        }
    }

    private void UpdateCosmeticSelectionAvailability(CommerceProductKind kind, bool isEnabled)
    {
        ObservableCollection<CosmeticSelectionItemViewModel> selections =
            kind == CommerceProductKind.Bubble ? BubbleSelections : ThrowableSelections;
        foreach (CosmeticSelectionItemViewModel selection in selections)
        {
            selection.IsEnabled = isEnabled;
            selection.IsPending = _pendingCosmeticIds.TryGetValue(kind, out string? pendingId)
                && StringComparer.Ordinal.Equals(pendingId, selection.CatalogItemId);
            selection.AnimationsEnabled = _coordinator.AnimationsEnabled;
        }
    }

    public void RefreshMonitors()
    {
        IReadOnlyList<MonitorOption> desired = _coordinator.GetMonitors();
        if (Monitors.SequenceEqual(desired))
        {
            return;
        }

        Monitors.Clear();
        foreach (MonitorOption monitor in desired)
        {
            Monitors.Add(monitor);
        }

        _isApplyingState = true;
        try
        {
            string? preferredMonitor = _state.Preferences.OverlayRegion.MonitorIdentifier;
            SelectedMonitorIdentifier = Monitors.Any(monitor =>
                    StringComparer.Ordinal.Equals(monitor.Identifier, preferredMonitor))
                ? preferredMonitor
                : Monitors.FirstOrDefault(monitor => monitor.IsPrimary)?.Identifier
                    ?? Monitors.FirstOrDefault()?.Identifier;
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private bool NicknameDraftMatchesSyncedState() =>
        StringComparer.Ordinal.Equals(
            ProfileValidator.NormalizeNickname(Nickname),
            ProfileValidator.NormalizeNickname(_syncedProfileNickname));

    private static (string Nickname, string CharacterId) GetSyncedProfileDraft(
        CoordinatorState state) =>
    (
        state.Profile?.Nickname ?? state.Preferences.CachedNickname ?? string.Empty,
        PixelCharacterCatalog.NormalizeId(
            state.Profile?.CharacterId ?? state.Preferences.CachedCharacterId)
    );

    private void ApplyRegionPreference()
    {
        if (_isApplyingState
            || SelectedEdgeIndex < 0
            || SelectedSpanIndex < 0
            || string.IsNullOrWhiteSpace(SelectedMonitorIdentifier))
        {
            return;
        }

        var edge = (OverlayEdge)Math.Clamp(SelectedEdgeIndex, 0, 3);
        var span = (OverlaySpan)Math.Clamp(SelectedSpanIndex, 0, 2);
        _ = RunCommandAsync(
            () => _coordinator.SetRegionAsync(new OverlayRegionPreference(
                edge,
                span,
                SelectedMonitorIdentifier)),
            null);
    }

    private RoomCardViewModel CreateRoomCard(Room room)
    {
        var card = new RoomCardViewModel(
            room,
            new AsyncRelayCommand(() => SwitchRoomAsync(room.Id)),
            new RelayCommand(() => ToggleRoom(room.Id)),
            new AsyncRelayCommand(() => UseInviteCodeAsync(room.Id)),
            new AsyncRelayCommand(() => LeaveRoomAsync(room.Id)),
            new AsyncRelayCommand(() => RenameRoomAsync(room.Id)),
            new AsyncRelayCommand(() => DeleteRoomAsync(room.Id)));
        UpdateRoomCard(card, room);
        return card;
    }

    private void UpdateRoomCard(RoomCardViewModel card, Room room)
    {
        bool isActive = room.Id == _state.ActiveRoomId;
        bool isOwner = room.OwnerId == _state.Profile?.Id;
        bool isExpanded = _expandedRoomIds.Contains(room.Id);
        bool mutationsEnabled = _state.GroupOperation == GroupOperation.Idle;
        bool isSwitching = _state.GroupOperation == GroupOperation.Switching
            && _state.SwitchingRoomId == room.Id;
        RoomMemberCardViewModel[] members = [.. room.Members.Select(member =>
            new RoomMemberCardViewModel(
                room.Id,
                member.UserId,
                member.Nickname,
                PixelCharacterCatalog.NormalizeId(member.CharacterId),
                member.UserId == room.OwnerId,
                member.UserId == _state.Profile?.Id,
                mutationsEnabled && isOwner && member.UserId != room.OwnerId,
                new AsyncRelayCommand(() => RemoveMemberAsync(room.Id, member.UserId))))];
        card.Update(
            room,
            room.InviteCodeReady
                ? I18n.Format("groups.details", room.Members.Count, room.InviteCodeHint)
                : I18n.Format("groups.detailsRotationRequired", room.Members.Count),
            isActive,
            isOwner,
            isExpanded,
            isSwitching ? I18n.Get("groups.switching") : I18n.Get("groups.join"),
            !isActive
                && !isSwitching
                && (_state.GroupOperation is GroupOperation.Idle or GroupOperation.Switching),
            isSwitching,
            room.InviteCodeReady
                ? I18n.Get("groups.copyInvite")
                : I18n.Get("groups.rotateInvite"),
            mutationsEnabled && (room.InviteCodeReady || isOwner),
            mutationsEnabled,
            mutationsEnabled && isOwner,
            members);
    }

    private async Task SwitchRoomAsync(Guid roomId)
    {
        if (roomId == _state.ActiveRoomId)
        {
            return;
        }

        await RunCommandAsync(
            () => _coordinator.SwitchRoomAsync(roomId),
            I18n.Get("groups.switched"));
    }

    private void ToggleRoom(Guid roomId)
    {
        if (!_expandedRoomIds.Add(roomId))
        {
            _expandedRoomIds.Remove(roomId);
        }

        RefreshRoomCards();
    }

    private async Task UseInviteCodeAsync(Guid roomId)
    {
        if (RoomById(roomId) is not { } room)
        {
            return;
        }

        if (!room.InviteCodeReady && room.OwnerId == _state.Profile?.Id)
        {
            if (!await _dialogs.ConfirmInviteCodeRotationAsync())
            {
                return;
            }

            await RunCommandAsync(
                () => _coordinator.RotateInviteCodeAsync(room.Id),
                I18n.Get("groups.inviteRotated"));
            return;
        }

        await RunCommandAsync(async () =>
        {
            if (!await _coordinator.CopyInviteCodeAsync(room.Id))
            {
                throw new InvalidOperationException(I18n.Get("groups.inviteMissing"));
            }
            Rooms.FirstOrDefault(card => card.Room.Id == room.Id)
                ?.ShowInviteCopyConfirmation();
        }, null);
    }

    private async Task RenameRoomAsync(Guid roomId)
    {
        if (RoomById(roomId) is not { } room)
        {
            return;
        }

        string? name = await _dialogs.PromptForRoomNameAsync(room.Name);
        if (name is null)
        {
            return;
        }

        await RunCommandAsync(
            () => _coordinator.RenameRoomAsync(room.Id, name),
            I18n.Get("groups.renamed"));
    }

    private async Task RemoveMemberAsync(Guid roomId, Guid userId)
    {
        RoomMember? member = RoomById(roomId)?.Members.FirstOrDefault(
            candidate => candidate.UserId == userId);
        if (member is null || !await _dialogs.ConfirmMemberRemovalAsync(member.Nickname))
        {
            return;
        }

        await RunCommandAsync(
            () => _coordinator.RemoveRoomMemberAsync(roomId, userId),
            I18n.Get("groups.memberRemoved"));
    }

    private async Task LeaveRoomAsync(Guid roomId)
    {
        if (RoomById(roomId) is not { } room)
        {
            return;
        }

        bool isOwner = room.OwnerId == _state.Profile?.Id;
        if (!await _dialogs.ConfirmRoomLeaveAsync(room.Name, isOwner))
        {
            return;
        }

        await RunCommandAsync(
            () => _coordinator.LeaveRoomAsync(room.Id),
            I18n.Get("groups.left"));
    }

    private async Task DeleteRoomAsync(Guid roomId)
    {
        if (RoomById(roomId) is not { } room)
        {
            return;
        }

        if (!await _dialogs.ConfirmRoomDeletionAsync(room.Name))
        {
            return;
        }

        await RunCommandAsync(
            () => _coordinator.DeleteRoomAsync(room.Id),
            I18n.Get("groups.deleted"));
    }

    private async Task<bool> RunCommandAsync(Func<Task> action, string? successMessage, Func<bool>? isCurrent = null)
    {
        try
        {
            await action();
            if (isCurrent?.Invoke() == false)
                return false;
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                RaiseNotice(successMessage, NoticeKind.Success);
            }
            return true;
        }
        catch (Exception exception)
        {
            if (isCurrent?.Invoke() == false)
                return false;
            RaiseNotice(exception.Message, NoticeKind.Error);
            return false;
        }
    }

    private Room? ActiveRoom() => _state.ActiveRoomId is { } roomId
        ? _state.Rooms.FirstOrDefault(room => room.Id == roomId)
        : null;

    private void RaiseConnectionNotice(CoordinatorState state)
    {
        bool hasNewError = !string.IsNullOrWhiteSpace(state.ErrorMessage)
            && !StringComparer.Ordinal.Equals(state.ErrorMessage, _previousState.ErrorMessage);
        if (hasNewError)
        {
            RaiseNotice(state.ErrorMessage!, NoticeKind.Warning);
        }
        else if (_hasAppliedState && state.Connected && !_previousState.Connected)
        {
            RaiseNotice(I18n.Get("connection.serverConnected"), NoticeKind.Success);
        }
        else if (_hasAppliedState && !state.Connected && _previousState.Connected)
        {
            RaiseNotice(I18n.Get("connection.serverReconnecting"), NoticeKind.Informational);
        }
    }

    private void RaiseNotice(string message, NoticeKind kind) =>
        NoticeRaised?.Invoke(new NoticeMessage(message, kind));

    private void RefreshRoomCards()
    {
        var desiredIds = _state.Rooms.Select(room => room.Id).ToHashSet();
        for (int index = Rooms.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(Rooms[index].Room.Id))
            {
                Rooms[index].Dispose();
                Rooms.RemoveAt(index);
            }
        }

        foreach (Room desired in _state.Rooms)
        {
            int existingIndex = IndexOfRoom(desired.Id);
            if (existingIndex < 0)
            {
                Rooms.Add(CreateRoomCard(desired));
                continue;
            }

            UpdateRoomCard(Rooms[existingIndex], desired);
        }
    }

    private int IndexOfRoom(Guid roomId)
    {
        for (int index = 0; index < Rooms.Count; index++)
        {
            if (Rooms[index].Room.Id == roomId)
            {
                return index;
            }
        }

        return -1;
    }

    private Room? RoomById(Guid roomId) =>
        _state.Rooms.FirstOrDefault(room => room.Id == roomId);

    private void RefreshValidationMetrics()
    {
        ValidationPathText = _coordinator.ValidationMetricsPath is { } path
            ? I18n.Format("metrics.exportPath", path)
            : I18n.Get("metrics.rendererNotStarted");
        ValidationMetricsSnapshot? summary = _coordinator.ValidationMetricsSummary;
        ValidationMetricsText = summary is null
            ? I18n.Get("metrics.noSamples")
            : I18n.Format(
                "metrics.summary",
                summary.ElapsedSeconds,
                summary.SampleCount,
                summary.MaximumFrameMilliseconds,
                summary.CurrentWorkingSetBytes / 1_048_576d,
                summary.PeakWorkingSetBytes / 1_048_576d,
                summary.MaximumGdiHandles,
                summary.MaximumUserHandles);
    }
}
