using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BetterCrewLinkKai.DotNet.Models;
using BetterCrewLinkKai.DotNet.Services;

namespace BetterCrewLinkKai.DotNet.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly SettingsService settingsService;
    private readonly GameLaunchService gameLaunchService;
    private readonly VoiceSessionService voiceSessionService;
    private readonly AudioDeviceService audioDeviceService;
    private readonly VoiceCaptureService voiceCaptureService;
    private readonly VoicePlaybackService voicePlaybackService;
    private readonly CosmeticCatalogService cosmeticCatalogService;
    private readonly AvatarImageService avatarImageService;
    private readonly HotkeyService hotkeyService;
    private readonly PublicLobbyBrowserService publicLobbyBrowserService;
    private readonly ObsOverlayService obsOverlayService = new();
    private readonly AmongUsProcessService amongUsProcessService;
    private readonly AmongUsMemoryReaderService amongUsMemoryReaderService;
    private AppSettings settings = new();
    private string lobbyCode = string.Empty;
    private string statusMessage = "Among Us を起動してください。";
    private bool isBusy;
    private bool isSettingsOpen;
    private bool isLaunchPlatformMenuOpen;
    private bool isGameOpen;
    private bool isInGameSession;
    private string waitingTitle = "Among Usの待機中";
    private string installedModLabel = "None";
    private string currentLobbyCode = "MENU";
    private string localPlayerName = "ROBBER";
    private Brush localPlayerBrush = new SolidColorBrush(Color.FromRgb(0x50, 0xEF, 0x39));
    private Brush localPlayerShadowBrush = new SolidColorBrush(Color.FromRgb(0x15, 0xA7, 0x42));
    private ImageSource? localAvatarImage;
    private string localHatFrontImage = string.Empty;
    private string localHatBackImage = string.Empty;
    private string localSkinImage = string.Empty;
    private string localVisorImage = string.Empty;
    private Thickness localHatMargin;
    private Thickness localSkinMargin;
    private Thickness localVisorMargin;
    private double localHatWidth;
    private double localSkinWidth;
    private double localVisorWidth;
    private AmongUsModType currentMod = AmongUsModType.None;
    private double microphoneLevel;
    private bool isMicrophoneMonitorRunning;
    private bool isVoiceEffectTestRunning;
    private int capturedVoiceFrames;
    private int transmittedVoiceFrames;
    private int queuedVoiceFrames;
    private bool isMuted;
    private bool isDeafened;
    private bool isPushToTalkHeld;
    private bool isImpostorRadioHeld;
    private bool lastTalkingState;
    private bool localIsTalking;
    private AmongUsState? lastAmongUsState;
    private readonly Dictionary<int, DateTimeOffset> remoteTalkingUntil = [];
    private bool isPublicLobbyBrowserOpen;
    private bool isPublicLobbySettingsOpen;
    private bool isPublicLobbyBrowserLoading;
    private string publicLobbyBrowserMessage = string.Empty;
    private bool isVoiceServerSettingsOpen;
    private string voiceServerUrl = "https://bettercrewl.ink";
    private string selectedVoiceServerUrl = "https://bettercrewl.ink";
    private bool isVoiceServerUrlValid = true;
    private CancellationTokenSource? pendingSettingsSave;

    public MainWindowViewModel()
        : this(new SettingsService(), new GameLaunchService(), new VoiceSessionService(), new AudioDeviceService(), new VoiceCaptureService(), new VoicePlaybackService(), new CosmeticCatalogService(), new AvatarImageService(), new HotkeyService(), new PublicLobbyBrowserService(), new AmongUsProcessService(), new AmongUsMemoryReaderService())
    {
    }

    public MainWindowViewModel(
        SettingsService settingsService,
        GameLaunchService gameLaunchService,
        VoiceSessionService voiceSessionService,
        AudioDeviceService audioDeviceService,
        VoiceCaptureService voiceCaptureService,
        VoicePlaybackService voicePlaybackService,
        CosmeticCatalogService cosmeticCatalogService,
        AvatarImageService avatarImageService,
        HotkeyService hotkeyService,
        PublicLobbyBrowserService publicLobbyBrowserService,
        AmongUsProcessService amongUsProcessService,
        AmongUsMemoryReaderService amongUsMemoryReaderService)
    {
        this.settingsService = settingsService;
        this.gameLaunchService = gameLaunchService;
        this.voiceSessionService = voiceSessionService;
        this.audioDeviceService = audioDeviceService;
        this.voiceCaptureService = voiceCaptureService;
        this.voicePlaybackService = voicePlaybackService;
        this.cosmeticCatalogService = cosmeticCatalogService;
        this.avatarImageService = avatarImageService;
        this.hotkeyService = hotkeyService;
        this.publicLobbyBrowserService = publicLobbyBrowserService;
        this.amongUsProcessService = amongUsProcessService;
        this.amongUsMemoryReaderService = amongUsMemoryReaderService;
        this.amongUsProcessService.ProcessChanged += OnAmongUsProcessChanged;
        this.amongUsMemoryReaderService.StateChanged += OnAmongUsStateChanged;
        this.amongUsMemoryReaderService.Error += OnAmongUsMemoryError;
        this.audioDeviceService.MicrophoneLevelChanged += OnMicrophoneLevelChanged;
        this.voiceCaptureService.FrameCaptured += OnVoiceFrameCaptured;
        this.voiceSessionService.StateChanged += OnVoiceSessionStateChanged;
        this.voiceSessionService.Error += OnVoiceSessionError;
        this.voiceSessionService.PeerDataPayloadQueued += OnPeerDataPayloadQueued;
        this.voiceSessionService.PeerAudioFrameQueued += OnPeerAudioFrameQueued;
        this.voiceSessionService.PeerAudioFrameReceived += OnPeerAudioFrameReceived;
        this.hotkeyService.HotkeyChanged += OnHotkeyChanged;
        this.publicLobbyBrowserService.LobbiesChanged += OnPublicLobbiesChanged;
        this.publicLobbyBrowserService.Error += OnPublicLobbyBrowserError;

        Platforms = new ObservableCollection<GamePlatform>(
            Enum.GetValues<GamePlatform>().Where(static platform => platform != GamePlatform.Custom));
        PushToTalkModes = new ObservableCollection<PushToTalkMode>(Enum.GetValues<PushToTalkMode>());
        Microphones = [];
        Speakers = [];
        OtherPlayers = [];
        PublicLobbies = [];
        VoiceServerUrls = [];
        LanguageOptions = new ObservableCollection<OptionViewModel>(
        [
            new("ja", "日本語"),
            new("en", "English"),
            new("es", "Español"),
            new("pt_BR", "Português"),
            new("fr", "Français"),
            new("de", "Deutsch"),
            new("ru", "Русский"),
            new("zh_CN", "简体中文"),
            new("ko", "한국어")
        ]);
        OverlayPositionOptions = new ObservableCollection<OptionViewModel>(
        [
            new("right", "右"),
            new("left", "左"),
            new("top-right", "右上"),
            new("top-left", "左上"),
            new("bottom-right", "右下"),
            new("bottom-left", "左下")
        ]);
        ModOptions = new ObservableCollection<OptionViewModel>(
            AmongUsMod.KnownMods.Select(static mod => new OptionViewModel(ModIdToProtocol(mod.Id), mod.Label)));

        LoadCommand = new RelayCommand(_ => LoadAsync());
        SaveCommand = new RelayCommand(_ => SaveAsync());
        LaunchGameCommand = new RelayCommand(_ => LaunchGameAsync());
        ToggleConnectionCommand = new RelayCommand(_ => ToggleConnectionAsync());
        RefreshAudioDevicesCommand = new RelayCommand(_ =>
        {
            RefreshAudioDevices();
            RestartMicrophoneMonitor();
            RestartVoiceCapture();
            return Task.CompletedTask;
        });
        TestSpeakerCommand = new RelayCommand(_ => TestSpeakerAsync());
        TestVoiceEffectCommand = new RelayCommand(_ =>
        {
            ToggleVoiceEffectTest();
            return Task.CompletedTask;
        });
        ToggleMuteCommand = new RelayCommand(_ =>
        {
            ToggleMute();
            return Task.CompletedTask;
        });
        ToggleDeafenCommand = new RelayCommand(_ =>
        {
            ToggleDeafen();
            return Task.CompletedTask;
        });
        ToggleSettingsCommand = new RelayCommand(_ =>
        {
            if (IsSettingsOpen)
            {
                IsSettingsOpen = false;
                return SaveAsync();
            }

            IsSettingsOpen = true;
            return Task.CompletedTask;
        });
        ToggleLaunchPlatformMenuCommand = new RelayCommand(_ =>
        {
            IsLaunchPlatformMenuOpen = !IsLaunchPlatformMenuOpen;
            return Task.CompletedTask;
        });
        SelectLaunchPlatformCommand = new RelayCommand(parameter =>
        {
            if (parameter is GamePlatform platform)
            {
                SelectedPlatform = platform;
                IsLaunchPlatformMenuOpen = false;
            }

            return Task.CompletedTask;
        });
        OpenPublicLobbyBrowserCommand = new RelayCommand(_ => OpenPublicLobbyBrowserAsync());
        ClosePublicLobbyBrowserCommand = new RelayCommand(_ => ClosePublicLobbyBrowserAsync());
        RequestPublicLobbyCodeCommand = new RelayCommand(parameter => RequestPublicLobbyCodeAsync(parameter));
        OpenPublicLobbySettingsCommand = new RelayCommand(_ =>
        {
            IsPublicLobbySettingsOpen = true;
            return Task.CompletedTask;
        });
        ClosePublicLobbySettingsCommand = new RelayCommand(_ =>
        {
            IsPublicLobbySettingsOpen = false;
            _ = SaveAsync();
            return Task.CompletedTask;
        });
        OpenVoiceServerSettingsCommand = new RelayCommand(_ =>
        {
            OpenVoiceServerSettings();
            return Task.CompletedTask;
        });
        CloseVoiceServerSettingsCommand = new RelayCommand(_ =>
        {
            IsVoiceServerSettingsOpen = false;
            return Task.CompletedTask;
        });
        SaveVoiceServerSettingsCommand = new RelayCommand(_ => SaveVoiceServerSettingsAsync());
        ResetVoiceServerCommand = new RelayCommand(_ => ResetVoiceServerAsync());
        RemoveVoiceServerCommand = new RelayCommand(_ => RemoveVoiceServerAsync());
        CopyObsOverlayUrlCommand = new RelayCommand(_ =>
        {
            Clipboard.SetText(ObsOverlayUrl);
            StatusMessage = "OBSオーバーレイURLをコピーしました。";
            return Task.CompletedTask;
        });
        RegenerateObsSecretCommand = new RelayCommand(_ => RegenerateObsSecretAsync());
        ResetSettingsCommand = new RelayCommand(_ => ResetSettingsAsync());
        OpenGitHubCommand = new RelayCommand(_ =>
        {
            OpenExternal("https://github.com/kuretoshi/BetterCrewLink/tree/voice_fixed");
            return Task.CompletedTask;
        });
        OpenDiscordCommand = new RelayCommand(_ =>
        {
            OpenExternal("https://discord.gg/jEyDrpBsmJ");
            return Task.CompletedTask;
        });
        CloseCommand = new RelayCommand(async _ =>
        {
            pendingSettingsSave?.Cancel();
            await settingsService.SaveAsync(Settings);
            obsOverlayService.Stop();
            Application.Current.Shutdown();
        });
    }

    public AppSettings Settings
    {
        get => settings;
        private set
        {
            if (SetProperty(ref settings, value))
            {
                OnPropertyChanged(nameof(SelectedPlatform));
                OnPropertyChanged(nameof(SelectedPushToTalkMode));
                OnPropertyChanged(nameof(SelectedMicrophone));
                OnPropertyChanged(nameof(SelectedSpeaker));
                OnPropertyChanged(nameof(PublicLobbyTitle));
                OnPropertyChanged(nameof(PublicLobbyLanguage));
                OnPropertyChanged(nameof(PublicLobbyMods));
                OnPropertyChanged(nameof(ObsOverlayUrl));
                OnPropertyChanged(nameof(ObsOverlayStatus));
                OnPropertyChanged(nameof(MasterVolume));
                OnPropertyChanged(nameof(CrewVolumeAsGhost));
                OnPropertyChanged(nameof(GhostVolumeAsImpostor));
                OnPropertyChanged(nameof(VoiceEffectStrength));
                OnPropertyChanged(nameof(MicrophoneGainEnabled));
                OnPropertyChanged(nameof(MicrophoneGain));
                OnPropertyChanged(nameof(MicSensitivityEnabled));
                OnPropertyChanged(nameof(MicSensitivity));
                OnPropertyChanged(nameof(VadEnabled));
                OnPropertyChanged(nameof(EchoCancellation));
                OnPropertyChanged(nameof(NoiseSuppression));
                OnPropertyChanged(nameof(EnableSpatialAudio));
                OnPropertyChanged(nameof(NatFix));
                OnPropertyChanged(nameof(MobileHost));
                OnPropertyChanged(nameof(AlwaysOnTop));
                OnPropertyChanged(nameof(EnableOverlay));
                OnPropertyChanged(nameof(CompactOverlay));
                OnPropertyChanged(nameof(MeetingOverlay));
                OnPropertyChanged(nameof(OverlayPosition));
                OnPropertyChanged(nameof(HardwareAcceleration));
                OnPropertyChanged(nameof(OldSampleDebug));
                OnPropertyChanged(nameof(Language));
                OnPropertyChanged(nameof(HideCode));
                OnPropertyChanged(nameof(ObsOverlay));
                OnPropertyChanged(nameof(DisplayLobbyCode));
            }
        }
    }

    public ObservableCollection<GamePlatform> Platforms { get; }

    public ObservableCollection<PushToTalkMode> PushToTalkModes { get; }

    public ObservableCollection<AudioDeviceInfo> Microphones { get; }

    public ObservableCollection<AudioDeviceInfo> Speakers { get; }

    public ObservableCollection<PlayerTileViewModel> OtherPlayers { get; }

    public ObservableCollection<PublicLobbyViewModel> PublicLobbies { get; }

    public ObservableCollection<string> VoiceServerUrls { get; }

    public ObservableCollection<OptionViewModel> LanguageOptions { get; }

    public ObservableCollection<OptionViewModel> OverlayPositionOptions { get; }

    public ObservableCollection<OptionViewModel> ModOptions { get; }

    public GamePlatform SelectedPlatform
    {
        get => Settings.LaunchPlatform;
        set
        {
            if (Settings.LaunchPlatform != value)
            {
            Settings.LaunchPlatform = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LaunchPlatformLabel));
            QueueSettingsSave();
        }
    }
    }

    public string LaunchPlatformLabel => Settings.LaunchPlatform switch
    {
        GamePlatform.Steam => "Steam",
        GamePlatform.Epic => "Epic",
        GamePlatform.Microsoft => "その他",
        GamePlatform.Custom => "その他",
        _ => Settings.LaunchPlatform.ToString()
    };

    public string SelectedMicrophone
    {
        get => Settings.Microphone;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "Default" : value;
            if (Settings.Microphone == next)
            {
                return;
            }

            Settings.Microphone = next;
            OnPropertyChanged();
            RestartMicrophoneMonitor();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public string SelectedSpeaker
    {
        get => Settings.Speaker;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "Default" : value;
            if (Settings.Speaker == next)
            {
                return;
            }

            Settings.Speaker = next;
            OnPropertyChanged();
            voicePlaybackService.SetSpeaker(Settings.Speaker);
            QueueSettingsSave();
        }
    }

    public PushToTalkMode SelectedPushToTalkMode
    {
        get => Settings.PushToTalkMode;
        set
        {
            if (Settings.PushToTalkMode != value)
            {
                Settings.PushToTalkMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTransmitting));
                _ = RefreshVadAsync();
                QueueSettingsSave();
            }
        }
    }

    public string LobbyCode
    {
        get => lobbyCode;
        set => SetProperty(ref lobbyCode, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public bool IsConnected => voiceSessionService.State == VoiceSessionState.Connected;

    public string ConnectionButtonText => IsConnected ? "切断" : "接続";

    public bool IsGameOpen
    {
        get => isGameOpen;
        private set
        {
            if (SetProperty(ref isGameOpen, value))
            {
                OnPropertyChanged(nameof(ShowWaitingView));
                OnPropertyChanged(nameof(ShowMenuView));
                OnPropertyChanged(nameof(ShowVoiceView));
            }
        }
    }

    public bool IsInGameSession
    {
        get => isInGameSession;
        private set
        {
            if (SetProperty(ref isInGameSession, value))
            {
                OnPropertyChanged(nameof(ShowWaitingView));
                OnPropertyChanged(nameof(ShowMenuView));
                OnPropertyChanged(nameof(ShowVoiceView));
            }
        }
    }

    public bool ShowWaitingView => !IsGameOpen;

    public bool ShowMenuView => IsGameOpen && !IsInGameSession;

    public bool ShowVoiceView => IsGameOpen && IsInGameSession;

    public bool IsDiscussionView => lastAmongUsState?.GameState == GameState.Discussion;

    public bool CanChangeLobbySettings => !IsInGameSession || lastAmongUsState?.IsHost == true;

    public string LobbySettingsModeText => CanChangeLobbySettings
        ? "このロビーの設定を変更できます。"
        : "ホストのロビー設定を使用しています。";

    private LobbySettings EffectiveLobbySettings => !CanChangeLobbySettings && voiceSessionService.HostLobbySettings is not null
        ? voiceSessionService.HostLobbySettings
        : Settings.LocalLobbySettings;

    public double EffectiveMaxDistance
    {
        get => EffectiveLobbySettings.MaxDistance;
        set
        {
            if (!CanChangeLobbySettings)
            {
                return;
            }

            Settings.LocalLobbySettings.MaxDistance = value;
            OnEffectiveLobbySettingsChanged();
            RefreshLobbySettingsFromSettings();
            QueueSettingsSave();
        }
    }

    public bool EffectivePublicLobbyOn
    {
        get => EffectiveLobbySettings.PublicLobbyOn;
        set
        {
            if (!CanChangeLobbySettings)
            {
                return;
            }

            Settings.LocalLobbySettings.PublicLobbyOn = value;
            OnEffectiveLobbySettingsChanged();
            RefreshLobbySettingsFromSettings();
            QueueSettingsSave();
        }
    }

    public bool EffectiveWallsBlockAudio
    {
        get => EffectiveLobbySettings.WallsBlockAudio;
        set => SetEffectiveLobbyBool(value, static settings => settings.WallsBlockAudio, static (settings, next) => settings.WallsBlockAudio = next, nameof(EffectiveWallsBlockAudio));
    }

    public bool EffectiveVisionHearing
    {
        get => EffectiveLobbySettings.VisionHearing;
        set => SetEffectiveLobbyBool(value, static settings => settings.VisionHearing, static (settings, next) => settings.VisionHearing = next, nameof(EffectiveVisionHearing));
    }

    public bool EffectiveHaunting
    {
        get => EffectiveLobbySettings.Haunting;
        set => SetEffectiveLobbyBool(value, static settings => settings.Haunting, static (settings, next) => settings.Haunting = next, nameof(EffectiveHaunting));
    }

    public bool EffectiveThirdPartyHaunting
    {
        get => EffectiveLobbySettings.ThirdPartyHaunting;
        set => SetEffectiveLobbyBool(value, static settings => settings.ThirdPartyHaunting, static (settings, next) => settings.ThirdPartyHaunting = next, nameof(EffectiveThirdPartyHaunting));
    }

    public bool EffectiveVoiceEffectEnabled
    {
        get => EffectiveLobbySettings.VoiceEffectEnabled;
        set => SetEffectiveLobbyBool(value, static settings => settings.VoiceEffectEnabled, static (settings, next) => settings.VoiceEffectEnabled = next, nameof(EffectiveVoiceEffectEnabled));
    }

    public bool EffectiveHearImpostorsInVents
    {
        get => EffectiveLobbySettings.HearImpostorsInVents;
        set => SetEffectiveLobbyBool(value, static settings => settings.HearImpostorsInVents, static (settings, next) => settings.HearImpostorsInVents = next, nameof(EffectiveHearImpostorsInVents));
    }

    public bool EffectiveImpostorsHearImpostorsInVent
    {
        get => EffectiveLobbySettings.ImpostorsHearImpostorsInVent;
        set => SetEffectiveLobbyBool(value, static settings => settings.ImpostorsHearImpostorsInVent, static (settings, next) => settings.ImpostorsHearImpostorsInVent = next, nameof(EffectiveImpostorsHearImpostorsInVent));
    }

    public bool EffectiveCommsSabotage
    {
        get => EffectiveLobbySettings.CommsSabotage;
        set => SetEffectiveLobbyBool(value, static settings => settings.CommsSabotage, static (settings, next) => settings.CommsSabotage = next, nameof(EffectiveCommsSabotage));
    }

    public bool EffectiveHearThroughCameras
    {
        get => EffectiveLobbySettings.HearThroughCameras;
        set => SetEffectiveLobbyBool(value, static settings => settings.HearThroughCameras, static (settings, next) => settings.HearThroughCameras = next, nameof(EffectiveHearThroughCameras));
    }

    public bool EffectiveImpostorRadioEnabled
    {
        get => EffectiveLobbySettings.ImpostorRadioEnabled;
        set => SetEffectiveLobbyBool(value, static settings => settings.ImpostorRadioEnabled, static (settings, next) => settings.ImpostorRadioEnabled = next, nameof(EffectiveImpostorRadioEnabled));
    }

    public bool EffectiveDeadOnly
    {
        get => EffectiveLobbySettings.DeadOnly;
        set => SetEffectiveLobbyBool(value, static settings => settings.DeadOnly, static (settings, next) => settings.DeadOnly = next, nameof(EffectiveDeadOnly));
    }

    public bool EffectiveMeetingGhostOnly
    {
        get => EffectiveLobbySettings.MeetingGhostOnly;
        set => SetEffectiveLobbyBool(value, static settings => settings.MeetingGhostOnly, static (settings, next) => settings.MeetingGhostOnly = next, nameof(EffectiveMeetingGhostOnly));
    }

    public string CurrentLobbyCode
    {
        get => currentLobbyCode;
        private set
        {
            if (SetProperty(ref currentLobbyCode, value))
            {
                OnPropertyChanged(nameof(DisplayLobbyCode));
            }
        }
    }

    public string DisplayLobbyCode => Settings.HideCode ? MaskLobbyCode(CurrentLobbyCode) : CurrentLobbyCode;

    public string LocalPlayerName
    {
        get => localPlayerName;
        private set => SetProperty(ref localPlayerName, value);
    }

    public Brush LocalPlayerBrush
    {
        get => localPlayerBrush;
        private set => SetProperty(ref localPlayerBrush, value);
    }

    public Brush LocalPlayerShadowBrush
    {
        get => localPlayerShadowBrush;
        private set => SetProperty(ref localPlayerShadowBrush, value);
    }

    public ImageSource? LocalAvatarImage
    {
        get => localAvatarImage;
        private set => SetProperty(ref localAvatarImage, value);
    }

    public string LocalHatFrontImage
    {
        get => localHatFrontImage;
        private set => SetProperty(ref localHatFrontImage, value);
    }

    public string LocalHatBackImage
    {
        get => localHatBackImage;
        private set => SetProperty(ref localHatBackImage, value);
    }

    public string LocalSkinImage
    {
        get => localSkinImage;
        private set => SetProperty(ref localSkinImage, value);
    }

    public string LocalVisorImage
    {
        get => localVisorImage;
        private set => SetProperty(ref localVisorImage, value);
    }

    public Thickness LocalHatMargin
    {
        get => localHatMargin;
        private set => SetProperty(ref localHatMargin, value);
    }

    public Thickness LocalSkinMargin
    {
        get => localSkinMargin;
        private set => SetProperty(ref localSkinMargin, value);
    }

    public Thickness LocalVisorMargin
    {
        get => localVisorMargin;
        private set => SetProperty(ref localVisorMargin, value);
    }

    public double LocalHatWidth
    {
        get => localHatWidth;
        private set => SetProperty(ref localHatWidth, value);
    }

    public double LocalSkinWidth
    {
        get => localSkinWidth;
        private set => SetProperty(ref localSkinWidth, value);
    }

    public double LocalVisorWidth
    {
        get => localVisorWidth;
        private set => SetProperty(ref localVisorWidth, value);
    }

    public double MicrophoneLevel
    {
        get => microphoneLevel;
        private set => SetProperty(ref microphoneLevel, value);
    }

    public bool IsMicrophoneMonitorRunning
    {
        get => isMicrophoneMonitorRunning;
        private set => SetProperty(ref isMicrophoneMonitorRunning, value);
    }

    public bool IsVoiceEffectTestRunning
    {
        get => isVoiceEffectTestRunning;
        private set
        {
            if (SetProperty(ref isVoiceEffectTestRunning, value))
            {
                OnPropertyChanged(nameof(VoiceEffectTestButtonText));
            }
        }
    }

    public string VoiceEffectTestButtonText => IsVoiceEffectTestRunning ? "ボイスエフェクト停止" : "ボイスエフェクトテスト";

    public double MasterVolume
    {
        get => Settings.MasterVolume;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.MasterVolume - next) < 0.001)
            {
                return;
            }

            Settings.MasterVolume = next;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public double CrewVolumeAsGhost
    {
        get => Settings.CrewVolumeAsGhost;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.CrewVolumeAsGhost - next) < 0.001)
            {
                return;
            }

            Settings.CrewVolumeAsGhost = next;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public double GhostVolumeAsImpostor
    {
        get => Settings.GhostVolumeAsImpostor;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.GhostVolumeAsImpostor - next) < 0.001)
            {
                return;
            }

            Settings.GhostVolumeAsImpostor = next;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public double VoiceEffectStrength
    {
        get => Settings.VoiceEffectStrength;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.VoiceEffectStrength - next) < 0.001)
            {
                return;
            }

            Settings.VoiceEffectStrength = next;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public bool MicrophoneGainEnabled
    {
        get => Settings.MicrophoneGainEnabled;
        set
        {
            if (Settings.MicrophoneGainEnabled == value)
            {
                return;
            }

            Settings.MicrophoneGainEnabled = value;
            OnPropertyChanged();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public double MicrophoneGain
    {
        get => Settings.MicrophoneGain;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.MicrophoneGain - next) < 0.001)
            {
                return;
            }

            Settings.MicrophoneGain = next;
            OnPropertyChanged();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public bool MicSensitivityEnabled
    {
        get => Settings.MicSensitivityEnabled;
        set
        {
            if (Settings.MicSensitivityEnabled == value)
            {
                return;
            }

            Settings.MicSensitivityEnabled = value;
            OnPropertyChanged();
            _ = RefreshVadAsync(force: true);
            QueueSettingsSave();
        }
    }

    public double MicSensitivity
    {
        get => Settings.MicSensitivity;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 1d), 3);
            if (Math.Abs(Settings.MicSensitivity - next) < 0.0001)
            {
                return;
            }

            Settings.MicSensitivity = next;
            OnPropertyChanged();
            _ = RefreshVadAsync(force: true);
            QueueSettingsSave();
        }
    }

    public bool VadEnabled
    {
        get => Settings.VadEnabled;
        set
        {
            if (Settings.VadEnabled == value)
            {
                return;
            }

            Settings.VadEnabled = value;
            OnPropertyChanged();
            _ = RefreshVadAsync(force: true);
            QueueSettingsSave();
        }
    }

    public bool EchoCancellation
    {
        get => Settings.EchoCancellation;
        set
        {
            if (Settings.EchoCancellation == value)
            {
                return;
            }

            Settings.EchoCancellation = value;
            OnPropertyChanged();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public bool NoiseSuppression
    {
        get => Settings.NoiseSuppression;
        set
        {
            if (Settings.NoiseSuppression == value)
            {
                return;
            }

            Settings.NoiseSuppression = value;
            OnPropertyChanged();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public bool EnableSpatialAudio
    {
        get => Settings.EnableSpatialAudio;
        set
        {
            if (Settings.EnableSpatialAudio == value)
            {
                return;
            }

            Settings.EnableSpatialAudio = value;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public bool NatFix
    {
        get => Settings.NatFix;
        set
        {
            if (Settings.NatFix == value)
            {
                return;
            }

            Settings.NatFix = value;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            OnPropertyChanged(nameof(PeerConnectionStatus));
            QueueSettingsSave();
        }
    }

    public bool MobileHost
    {
        get => Settings.MobileHost;
        set
        {
            if (Settings.MobileHost == value)
            {
                return;
            }

            Settings.MobileHost = value;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public bool AlwaysOnTop
    {
        get => Settings.AlwaysOnTop;
        set
        {
            if (Settings.AlwaysOnTop == value)
            {
                return;
            }

            Settings.AlwaysOnTop = value;
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool EnableOverlay
    {
        get => Settings.EnableOverlay;
        set
        {
            if (Settings.EnableOverlay == value)
            {
                return;
            }

            Settings.EnableOverlay = value;
            OnPropertyChanged();
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public bool CompactOverlay
    {
        get => Settings.CompactOverlay;
        set
        {
            if (Settings.CompactOverlay == value)
            {
                return;
            }

            Settings.CompactOverlay = value;
            OnPropertyChanged();
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public bool MeetingOverlay
    {
        get => Settings.MeetingOverlay;
        set
        {
            if (Settings.MeetingOverlay == value)
            {
                return;
            }

            Settings.MeetingOverlay = value;
            OnPropertyChanged();
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public string OverlayPosition
    {
        get => Settings.OverlayPosition;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "right" : value;
            if (string.Equals(Settings.OverlayPosition, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.OverlayPosition = next;
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool HardwareAcceleration
    {
        get => Settings.HardwareAcceleration;
        set
        {
            if (Settings.HardwareAcceleration == value)
            {
                return;
            }

            Settings.HardwareAcceleration = value;
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool OldSampleDebug
    {
        get => Settings.OldSampleDebug;
        set
        {
            if (Settings.OldSampleDebug == value)
            {
                return;
            }

            Settings.OldSampleDebug = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VoiceCaptureStatus));
            QueueSettingsSave();
        }
    }

    public string Language
    {
        get => Settings.Language;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "ja" : value;
            if (string.Equals(Settings.Language, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.Language = next;
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool HideCode
    {
        get => Settings.HideCode;
        set
        {
            if (Settings.HideCode == value)
            {
                return;
            }

            Settings.HideCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayLobbyCode));
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public bool ObsOverlay
    {
        get => Settings.ObsOverlay;
        set
        {
            if (Settings.ObsOverlay == value)
            {
                return;
            }

            Settings.ObsOverlay = value;
            OnPropertyChanged();
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public string VoiceCaptureStatus
    {
        get
        {
            var status = $"voice frames: {capturedVoiceFrames}, tx: {transmittedVoiceFrames}, queued: {queuedVoiceFrames}";
            if (!Settings.OldSampleDebug || voiceCaptureService.WaveFormat is null)
            {
                return status;
            }

            var format = voiceCaptureService.WaveFormat;
            return $"{status}, mic: {format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch/{format.Encoding}";
        }
    }

    public string PeerConnectionStatus => $"peers: {voiceSessionService.PeerConnections.Count}, NAT fix: {(voiceSessionService.UseNatFix ? "on" : "off")}, ICE: {voiceSessionService.IceServers.Count}";

    public string ObsOverlayStatus => Settings.ObsOverlay
        ? $"OBS overlay: {ObsOverlayUrl}"
        : "OBS overlay: disabled";

    public string ObsOverlayUrl => $"{ObsOverlayService.Url}?secret={Uri.EscapeDataString(Settings.ObsSecret ?? string.Empty)}";

    public bool IsMuted
    {
        get => isMuted;
        private set
        {
            if (SetProperty(ref isMuted, value))
            {
                OnPropertyChanged(nameof(IsTransmitting));
                voiceSessionService.SetLocalMuteState(IsMuted, IsDeafened);
                RefreshObsOverlayService();
            }
        }
    }

    public bool IsDeafened
    {
        get => isDeafened;
        private set
        {
            if (SetProperty(ref isDeafened, value))
            {
                voicePlaybackService.SetDeafened(value);
                OnPropertyChanged(nameof(IsTransmitting));
                voiceSessionService.SetLocalMuteState(IsMuted, IsDeafened);
                RefreshObsOverlayService();
            }
        }
    }

    public bool IsPushToTalkHeld
    {
        get => isPushToTalkHeld;
        private set
        {
            if (SetProperty(ref isPushToTalkHeld, value))
            {
                OnPropertyChanged(nameof(IsTransmitting));
            }
        }
    }

    public bool IsImpostorRadioHeld
    {
        get => isImpostorRadioHeld;
        private set => SetProperty(ref isImpostorRadioHeld, value);
    }

    public bool IsTransmitting => !IsMuted && !IsDeafened && Settings.PushToTalkMode switch
    {
        PushToTalkMode.PushToTalk => IsPushToTalkHeld,
        PushToTalkMode.PushToMute => !IsPushToTalkHeld,
        _ => true
    };

    public bool LocalIsTalking
    {
        get => localIsTalking;
        private set
        {
            if (SetProperty(ref localIsTalking, value))
            {
                OnPropertyChanged(nameof(LocalRingBrush));
                OnPropertyChanged(nameof(LocalRingThickness));
                OnPropertyChanged(nameof(LocalRingGlowOpacity));
            }
        }
    }

    public Brush LocalRingBrush => LocalIsTalking
        ? new SolidColorBrush(Color.FromRgb(0x32, 0xFF, 0x7E))
        : new SolidColorBrush(Color.FromRgb(0x3C, 0x37, 0x46));

    public double LocalRingThickness => LocalIsTalking ? 3.5 : 1.3;

    public double LocalRingGlowOpacity => LocalIsTalking ? 0.9 : 0;

    public string WaitingTitle
    {
        get => waitingTitle;
        private set => SetProperty(ref waitingTitle, value);
    }

    public string InstalledModLabel
    {
        get => installedModLabel;
        private set => SetProperty(ref installedModLabel, value);
    }

    public bool IsSettingsOpen
    {
        get => isSettingsOpen;
        set => SetProperty(ref isSettingsOpen, value);
    }

    public bool IsLaunchPlatformMenuOpen
    {
        get => isLaunchPlatformMenuOpen;
        set => SetProperty(ref isLaunchPlatformMenuOpen, value);
    }

    public bool IsPublicLobbyBrowserOpen
    {
        get => isPublicLobbyBrowserOpen;
        private set => SetProperty(ref isPublicLobbyBrowserOpen, value);
    }

    public bool IsPublicLobbySettingsOpen
    {
        get => isPublicLobbySettingsOpen;
        set => SetProperty(ref isPublicLobbySettingsOpen, value);
    }

    public bool IsPublicLobbyBrowserLoading
    {
        get => isPublicLobbyBrowserLoading;
        private set => SetProperty(ref isPublicLobbyBrowserLoading, value);
    }

    public string PublicLobbyBrowserMessage
    {
        get => publicLobbyBrowserMessage;
        private set => SetProperty(ref publicLobbyBrowserMessage, value);
    }

    public string PublicLobbyTitle
    {
        get => Settings.LocalLobbySettings.PublicLobbyTitle;
        set
        {
            if (Settings.LocalLobbySettings.PublicLobbyTitle == value)
            {
                return;
            }

            Settings.LocalLobbySettings.PublicLobbyTitle = value ?? string.Empty;
            OnPropertyChanged();
            RefreshLobbySettingsFromSettings();
            QueueSettingsSave();
        }
    }

    public string PublicLobbyLanguage
    {
        get => Settings.LocalLobbySettings.PublicLobbyLanguage;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "ja" : value;
            if (Settings.LocalLobbySettings.PublicLobbyLanguage == next)
            {
                return;
            }

            Settings.LocalLobbySettings.PublicLobbyLanguage = next;
            OnPropertyChanged();
            RefreshLobbySettingsFromSettings();
            QueueSettingsSave();
        }
    }

    public string PublicLobbyMods
    {
        get => Settings.LocalLobbySettings.PublicLobbyMods;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "NONE" : value;
            if (Settings.LocalLobbySettings.PublicLobbyMods == next)
            {
                return;
            }

            Settings.LocalLobbySettings.PublicLobbyMods = next;
            OnPropertyChanged();
            RefreshLobbySettingsFromSettings();
            QueueSettingsSave();
        }
    }

    public bool IsVoiceServerSettingsOpen
    {
        get => isVoiceServerSettingsOpen;
        private set => SetProperty(ref isVoiceServerSettingsOpen, value);
    }

    public string VoiceServerUrl
    {
        get => voiceServerUrl;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref voiceServerUrl, next))
            {
                IsVoiceServerUrlValid = ValidateServerUrl(next);
                OnPropertyChanged(nameof(CanRemoveVoiceServer));
            }
        }
    }

    public string SelectedVoiceServerUrl
    {
        get => selectedVoiceServerUrl;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref selectedVoiceServerUrl, next) && !string.IsNullOrWhiteSpace(next))
            {
                VoiceServerUrl = next;
            }
        }
    }

    public bool IsVoiceServerUrlValid
    {
        get => isVoiceServerUrlValid;
        private set => SetProperty(ref isVoiceServerUrlValid, value);
    }

    public bool CanRemoveVoiceServer => VoiceServerUrls.Count > 1 && VoiceServerUrls.Contains(NormalizeServerUrl(VoiceServerUrl));

    public void UpdateShortcut(string settingName, string shortcut)
    {
        switch (settingName)
        {
            case nameof(AppSettings.PushToTalkShortcut):
                Settings.PushToTalkShortcut = shortcut;
                break;
            case nameof(AppSettings.MuteShortcut):
                Settings.MuteShortcut = shortcut;
                break;
            case nameof(AppSettings.DeafenShortcut):
                Settings.DeafenShortcut = shortcut;
                break;
            case nameof(AppSettings.ImpostorRadioShortcut):
                Settings.ImpostorRadioShortcut = shortcut;
                break;
            default:
                return;
        }

        ConfigureHotkeys();
        _ = settingsService.SaveAsync(Settings);
        OnPropertyChanged(nameof(Settings));
    }

    public ICommand LoadCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand LaunchGameCommand { get; }

    public ICommand ToggleConnectionCommand { get; }

    public ICommand RefreshAudioDevicesCommand { get; }

    public ICommand TestSpeakerCommand { get; }

    public ICommand TestVoiceEffectCommand { get; }

    public ICommand ToggleMuteCommand { get; }

    public ICommand ToggleDeafenCommand { get; }

    public ICommand ToggleSettingsCommand { get; }

    public ICommand ToggleLaunchPlatformMenuCommand { get; }

    public ICommand SelectLaunchPlatformCommand { get; }

    public ICommand OpenPublicLobbyBrowserCommand { get; }

    public ICommand ClosePublicLobbyBrowserCommand { get; }

    public ICommand RequestPublicLobbyCodeCommand { get; }

    public ICommand OpenPublicLobbySettingsCommand { get; }

    public ICommand ClosePublicLobbySettingsCommand { get; }

    public ICommand OpenVoiceServerSettingsCommand { get; }

    public ICommand CloseVoiceServerSettingsCommand { get; }

    public ICommand SaveVoiceServerSettingsCommand { get; }

    public ICommand ResetVoiceServerCommand { get; }

    public ICommand RemoveVoiceServerCommand { get; }

    public ICommand CopyObsOverlayUrlCommand { get; }

    public ICommand RegenerateObsSecretCommand { get; }

    public ICommand ResetSettingsCommand { get; }

    public ICommand OpenGitHubCommand { get; }

    public ICommand OpenDiscordCommand { get; }

    public ICommand CloseCommand { get; }

    private async Task LoadAsync()
    {
        Settings = await settingsService.LoadAsync();
        RefreshVoiceServerUrls();
        StatusMessage = $"設定を読み込みました: {settingsService.SettingsPath}";
        await cosmeticCatalogService.InitializeAsync();
        RefreshAudioDevices();
        RestartMicrophoneMonitor();
        RestartVoiceCapture();
        voicePlaybackService.SetSpeaker(Settings.Speaker);
        ConfigureHotkeys();
        hotkeyService.Start();
        RefreshObsOverlayService();
        _ = voiceSessionService.ConnectAsync(Settings.ServerUrl, "MENU");
        amongUsProcessService.Start();
        amongUsMemoryReaderService.Start();
    }

    private async Task SaveAsync()
    {
        pendingSettingsSave?.Cancel();
        await settingsService.SaveAsync(Settings);
        RefreshVoiceServerUrls();
        ConfigureHotkeys();
        RestartVoiceCapture();
        voicePlaybackService.SetSpeaker(Settings.Speaker);
        if (lastAmongUsState is not null)
        {
            _ = voiceSessionService.UpdateGameStateAsync(lastAmongUsState, Settings);
        }

        if (lastAmongUsState?.IsHost == true)
        {
            voiceSessionService.BroadcastLobbySettings(Settings);
        }

        OnPropertyChanged(nameof(DisplayLobbyCode));
        RefreshObsOverlayService();
        StatusMessage = "設定を保存しました。";
    }

    private void QueueSettingsSave()
    {
        var previousSave = pendingSettingsSave;
        var currentSave = new CancellationTokenSource();
        pendingSettingsSave = currentSave;
        previousSave?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, currentSave.Token);
                await settingsService.SaveAsync(Settings);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusMessage = $"設定の自動保存に失敗しました: {ex.Message}";
                });
            }
            finally
            {
                if (ReferenceEquals(pendingSettingsSave, currentSave))
                {
                    pendingSettingsSave = null;
                }

                currentSave.Dispose();
            }
        });
    }

    private void OpenVoiceServerSettings()
    {
        RefreshVoiceServerUrls();
        VoiceServerUrl = Settings.ServerUrl;
        SelectedVoiceServerUrl = VoiceServerUrls.Contains(NormalizeServerUrl(Settings.ServerUrl))
            ? NormalizeServerUrl(Settings.ServerUrl)
            : string.Empty;
        IsVoiceServerUrlValid = ValidateServerUrl(VoiceServerUrl);
        IsVoiceServerSettingsOpen = true;
    }

    private async Task SaveVoiceServerSettingsAsync()
    {
        var normalized = NormalizeServerUrl(VoiceServerUrl);
        if (!ValidateServerUrl(normalized))
        {
            IsVoiceServerUrlValid = false;
            StatusMessage = "ボイスサーバー URL が正しくありません。";
            return;
        }

        Settings.ServerUrl = normalized;
        Settings.ServerUrls = NormalizeServerUrls([normalized, ..VoiceServerUrls]);
        await settingsService.SaveAsync(Settings);
        RefreshVoiceServerUrls();
        IsVoiceServerSettingsOpen = false;
        StatusMessage = $"ボイスサーバーを変更しました: {Settings.ServerUrl}";
        QueueVoiceServerReconnect();
    }

    private async Task ResetVoiceServerAsync()
    {
        VoiceServerUrl = "https://bettercrewl.ink";
        Settings.ServerUrl = VoiceServerUrl;
        Settings.ServerUrls = [VoiceServerUrl];
        await settingsService.SaveAsync(Settings);
        RefreshVoiceServerUrls();
        IsVoiceServerSettingsOpen = false;
        StatusMessage = "ボイスサーバーをデフォルトに戻しました。";
        QueueVoiceServerReconnect();
    }

    private async Task RemoveVoiceServerAsync()
    {
        var normalized = NormalizeServerUrl(VoiceServerUrl);
        var nextUrls = VoiceServerUrls
            .Where(url => !string.Equals(url, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nextUrls.Count == 0)
        {
            nextUrls.Add("https://bettercrewl.ink");
        }

        Settings.ServerUrls = nextUrls;
        Settings.ServerUrl = nextUrls[0];
        VoiceServerUrl = Settings.ServerUrl;
        await settingsService.SaveAsync(Settings);
        RefreshVoiceServerUrls();
        StatusMessage = "保存済みボイスサーバーを削除しました。";
        QueueVoiceServerReconnect();
    }

    private async Task RegenerateObsSecretAsync()
    {
        Settings.ObsSecret = Guid.NewGuid().ToString("N");
        await settingsService.SaveAsync(Settings);
        RefreshObsOverlayService();
        StatusMessage = "OBSオーバーレイURLを再生成しました。OBS側のURLも貼り替えてください。";
    }

    private async Task ResetSettingsAsync()
    {
        var result = MessageBox.Show(
            "設定をデフォルトに戻しますか？",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Settings = new AppSettings();
        RefreshVoiceServerUrls();
        RefreshAudioDevices();
        RestartMicrophoneMonitor();
        RestartVoiceCapture();
        voicePlaybackService.SetSpeaker(Settings.Speaker);
        ConfigureHotkeys();
        await settingsService.SaveAsync(Settings);
        RefreshObsOverlayService();
        StatusMessage = "設定をデフォルトに戻しました。";
        QueueVoiceServerReconnect();
    }

    private async Task OpenPublicLobbyBrowserAsync()
    {
        IsPublicLobbyBrowserOpen = true;
        IsPublicLobbyBrowserLoading = true;
        PublicLobbyBrowserMessage = "公開ロビーを取得しています。";
        PublicLobbies.Clear();

        try
        {
            await publicLobbyBrowserService.StartAsync(Settings.ServerUrl);
            PublicLobbyBrowserMessage = "公開ロビーを取得中です。";
        }
        catch (Exception ex)
        {
            IsPublicLobbyBrowserLoading = false;
            PublicLobbyBrowserMessage = $"公開ロビーに接続できませんでした: {ex.Message}";
        }
    }

    private async Task ClosePublicLobbyBrowserAsync()
    {
        IsPublicLobbyBrowserOpen = false;
        IsPublicLobbyBrowserLoading = false;
        PublicLobbyBrowserMessage = string.Empty;
        await publicLobbyBrowserService.StopAsync();
    }

    private async Task RequestPublicLobbyCodeAsync(object? parameter)
    {
        if (parameter is not PublicLobbyViewModel lobby)
        {
            return;
        }

        PublicLobbyBrowserMessage = "ロビーコードを取得しています。";
        await publicLobbyBrowserService.RequestLobbyCodeAsync(lobby.Id, (state, codeOrError, server, _) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                PublicLobbyBrowserMessage = state == 0
                    ? $"ロビーコード: {codeOrError} / リージョン: {server}"
                    : $"ロビーコード取得エラー: {codeOrError}";
            });
        });
    }

    private void RefreshVoiceServerUrls()
    {
        var urls = NormalizeServerUrls(Settings.ServerUrls.Count > 0 ? Settings.ServerUrls : [Settings.ServerUrl]);
        if (!urls.Contains(NormalizeServerUrl(Settings.ServerUrl), StringComparer.OrdinalIgnoreCase) &&
            ValidateServerUrl(Settings.ServerUrl))
        {
            urls.Insert(0, NormalizeServerUrl(Settings.ServerUrl));
        }

        if (urls.Count == 0)
        {
            urls.Add("https://bettercrewl.ink");
        }

        Settings.ServerUrls = urls;
        VoiceServerUrls.Clear();
        foreach (var url in urls)
        {
            VoiceServerUrls.Add(url);
        }

        OnPropertyChanged(nameof(CanRemoveVoiceServer));
    }

    private void QueueVoiceServerReconnect()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await voiceSessionService.DisconnectAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var connectTask = voiceSessionService.ConnectAsync(Settings.ServerUrl, CurrentLobbyCode, timeout.Token);
                var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(6)));
                if (completed != connectTask)
                {
                    throw new TimeoutException("接続がタイムアウトしました。");
                }

                await connectTask;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    RefreshConnectionState();
                    StatusMessage = $"ボイスサーバーへ再接続しました: {Settings.ServerUrl}";
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusMessage = $"ボイスサーバーへの再接続に失敗しました: {ex.Message}";
                });
            }
        });
    }

    private Task LaunchGameAsync()
    {
        try
        {
            gameLaunchService.Launch(Settings.LaunchPlatform, Settings.CustomPlatforms);
            StatusMessage = $"{Settings.LaunchPlatform} 版 Among Us を起動しました。";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            MessageBox.Show(ex.Message, "起動エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }

    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await voiceSessionService.DisconnectAsync();
            RefreshConnectionState();
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "ボイスサーバーへ接続しています。";
            await voiceSessionService.ConnectAsync(Settings.ServerUrl, LobbyCode);
            StatusMessage = voiceSessionService.StatusText;
        }
        catch (Exception ex)
        {
            await voiceSessionService.DisconnectAsync();
            StatusMessage = ex.Message;
            MessageBox.Show(ex.Message, "接続エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            RefreshConnectionState();
        }
    }

    private void RefreshConnectionState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionButtonText));
        StatusMessage = voiceSessionService.StatusText;
    }

    private void RefreshVoiceMixFromSettings()
    {
        var state = voiceSessionService.CurrentState ?? lastAmongUsState;
        if (state is null)
        {
            return;
        }

        _ = voiceSessionService.UpdateGameStateAsync(state, Settings);
        voicePlaybackService.ApplyMix(voiceSessionService.Peers);
        if (lastAmongUsState is not null)
        {
            UpdateOtherPlayers(state);
        }

        RefreshObsOverlayService();
    }

    private void RefreshLobbySettingsFromSettings()
    {
        OnEffectiveLobbySettingsChanged();
        var state = voiceSessionService.CurrentState ?? lastAmongUsState;
        if (state is null)
        {
            return;
        }

        _ = voiceSessionService.UpdateGameStateAsync(state, Settings);
        if (state.IsHost)
        {
            voiceSessionService.BroadcastLobbySettings(Settings);
        }

        voicePlaybackService.ApplyMix(voiceSessionService.Peers);
        UpdateOtherPlayers(state);
        RefreshObsOverlayService();
    }

    private void RefreshAudioDevices()
    {
        Microphones.Clear();
        foreach (var device in audioDeviceService.GetMicrophones())
        {
            Microphones.Add(device);
        }

        Speakers.Clear();
        foreach (var device in audioDeviceService.GetSpeakers())
        {
            Speakers.Add(device);
        }
    }

    private async Task TestSpeakerAsync()
    {
        try
        {
            await audioDeviceService.PlaySpeakerTestAsync(Settings.Speaker, Settings.MasterVolume);
        }
        catch (Exception ex)
        {
            StatusMessage = $"スピーカーテストに失敗しました: {ex.Message}";
        }
    }

    private void RestartMicrophoneMonitor()
    {
        try
        {
            audioDeviceService.StartMicrophoneMonitor(Settings.Microphone);
            IsMicrophoneMonitorRunning = true;
        }
        catch (Exception ex)
        {
            IsMicrophoneMonitorRunning = false;
            MicrophoneLevel = 0;
            StatusMessage = $"マイク入力を開始できませんでした: {ex.Message}";
        }
    }

    private void RestartVoiceCapture()
    {
        if (IsVoiceEffectTestRunning)
        {
            return;
        }

        try
        {
            voiceCaptureService.Start(
                Settings.Microphone,
                Settings.MicrophoneGain,
                Settings.MicrophoneGainEnabled,
                Settings.NoiseSuppression,
                Settings.EchoCancellation);
            var talking = ShouldTransmitVoice();
            LocalIsTalking = talking;
            voiceCaptureService.SetTransmitting(talking);
        }
        catch (Exception ex)
        {
            StatusMessage = $"通話用マイク入力を開始できませんでした: {ex.Message}";
        }
    }

    private void ToggleVoiceEffectTest()
    {
        try
        {
            if (IsVoiceEffectTestRunning)
            {
                audioDeviceService.StopVoiceEffectTest();
                IsVoiceEffectTestRunning = false;
                RestartMicrophoneMonitor();
                RestartVoiceCapture();
                return;
            }

            audioDeviceService.StopMicrophoneMonitor();
            voiceCaptureService.Stop();
            IsMicrophoneMonitorRunning = false;
            audioDeviceService.StartVoiceEffectTest(
                Settings.Microphone,
                Settings.Speaker,
                Settings.VoiceEffectStrength,
                Settings.MasterVolume);
            IsVoiceEffectTestRunning = true;
        }
        catch (Exception ex)
        {
            audioDeviceService.StopVoiceEffectTest();
            IsVoiceEffectTestRunning = false;
            RestartMicrophoneMonitor();
            RestartVoiceCapture();
            StatusMessage = $"ボイスエフェクトテストに失敗しました: {ex.Message}";
        }
    }

    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        if (IsDeafened && IsMuted)
        {
            IsDeafened = false;
            IsMuted = false;
        }

        if (IsMuted)
        {
            IsPushToTalkHeld = false;
        }

        _ = RefreshVadAsync(force: true);
    }

    private void ToggleDeafen()
    {
        IsDeafened = !IsDeafened;
        if (IsDeafened)
        {
            IsMuted = true;
            IsPushToTalkHeld = false;
        }

        _ = RefreshVadAsync(force: true);
    }

    private void OnMicrophoneLevelChanged(object? sender, double level)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            MicrophoneLevel = level;
            _ = RefreshVadAsync();
        });
    }

    private void OnVoiceFrameCaptured(object? sender, VoiceAudioFrame frame)
    {
        capturedVoiceFrames++;
        if (frame.IsTransmitting)
        {
            transmittedVoiceFrames++;
            voiceSessionService.QueueLocalAudioFrame(frame);
        }

        if ((capturedVoiceFrames % 50) == 0)
        {
            Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(VoiceCaptureStatus)));
        }
    }

    private void ConfigureHotkeys()
    {
        hotkeyService.Configure(
            Settings.PushToTalkShortcut,
            Settings.MuteShortcut,
            Settings.DeafenShortcut,
            Settings.ImpostorRadioShortcut);
    }

    private void OnHotkeyChanged(object? sender, HotkeyEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            switch (e.Action)
            {
                case "push-to-talk":
                    IsPushToTalkHeld = e.IsPressed;
                    _ = RefreshVadAsync();
                    break;
                case "impostor-radio":
                    SetImpostorRadioHeld(e.IsPressed);
                    break;
                case "mute" when e.IsPressed:
                    ToggleMute();
                    break;
                case "deafen" when e.IsPressed:
                    ToggleDeafen();
                    break;
            }
        });
    }

    private async Task RefreshVadAsync(bool force = false)
    {
        var talking = ShouldTransmitVoice();
        LocalIsTalking = talking;
        voiceCaptureService.SetTransmitting(talking);
        if (!force && talking == lastTalkingState)
        {
            return;
        }

        lastTalkingState = talking;
        await voiceSessionService.EmitVadAsync(talking);
    }

    private bool ShouldTransmitVoice()
    {
        var threshold = Settings.MicSensitivityEnabled ? Settings.MicSensitivity * 100d : 1.5d;
        return IsTransmitting && (!Settings.VadEnabled || MicrophoneLevel >= threshold);
    }

    private void SetImpostorRadioHeld(bool isPressed)
    {
        var localPlayer = lastAmongUsState?.Players.FirstOrDefault(static player => player.IsLocal);
        var canUseRadio = Settings.LocalLobbySettings.ImpostorRadioEnabled &&
                          localPlayer?.IsImpostor == true &&
                          lastAmongUsState?.GameState == GameState.Tasks;

        IsImpostorRadioHeld = isPressed && canUseRadio;
        voiceSessionService.SetLocalImpostorRadio(IsImpostorRadioHeld);
        StatusMessage = IsImpostorRadioHeld ? "インポスターラジオを使用中です。" : StatusMessage;
    }

    private void OnAmongUsProcessChanged(object? sender, AmongUsProcessInfo? processInfo)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsGameOpen = processInfo is not null;
            OnPropertyChanged(nameof(ShowWaitingView));
            OnPropertyChanged(nameof(ShowMenuView));
            OnPropertyChanged(nameof(ShowVoiceView));
            WaitingTitle = "Among Usの待機中";
            InstalledModLabel = processInfo?.InstalledMod.Label ?? "None";
            currentMod = processInfo?.InstalledMod.Id ?? AmongUsModType.None;
            amongUsMemoryReaderService.SetProcess(processInfo);
            if (processInfo is null)
            {
                IsInGameSession = false;
                CurrentLobbyCode = "MENU";
                lastAmongUsState = null;
                OnPropertyChanged(nameof(IsDiscussionView));
                voicePlaybackService.Clear();
                OnPropertyChanged(nameof(CanChangeLobbySettings));
                OnPropertyChanged(nameof(LobbySettingsModeText));
                OnEffectiveLobbySettingsChanged();
                RefreshObsOverlayService();
            }
            StatusMessage = processInfo is null
                ? "Among Us process is not running."
                : $"Among Us PID {processInfo.ProcessId}, {(processInfo.Is64Bit ? "x64" : "x86")}, mod: {processInfo.InstalledMod.Label}";
        });
    }

    private void OnAmongUsStateChanged(object? sender, AmongUsState state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            lastAmongUsState = state;
            IsInGameSession = state.GameState is GameState.Lobby or GameState.Tasks or GameState.Discussion;
            OnPropertyChanged(nameof(IsDiscussionView));
            OnPropertyChanged(nameof(CanChangeLobbySettings));
            OnPropertyChanged(nameof(LobbySettingsModeText));
            OnEffectiveLobbySettingsChanged();
            CurrentLobbyCode = state.LobbyCode;
            var localPlayer = state.Players.FirstOrDefault(static player => player.IsLocal);
            if (localPlayer is not null)
            {
                LocalPlayerName = localPlayer.Name;
                var colors = PlayerColorPalette.Get(localPlayer.ColorId, state.PlayerColors);
                LocalPlayerBrush = new SolidColorBrush(colors.Main);
                LocalPlayerShadowBrush = new SolidColorBrush(colors.Shadow);
                LocalAvatarImage = avatarImageService.GetPlayerImage(colors.Main, colors.Shadow);
                var cosmetics = cosmeticCatalogService.GetImages(localPlayer, currentMod);
                LocalHatFrontImage = cosmetics.HatFront;
                LocalHatBackImage = cosmetics.HatBack;
                LocalSkinImage = cosmetics.Skin;
                LocalVisorImage = cosmetics.Visor;
                LocalHatMargin = ToBodyCosmeticPlacementMargin(cosmetics.Hat, 78);
                LocalSkinMargin = ToSkinPlacementMargin(cosmetics.SkinPlacement, 78);
                LocalVisorMargin = ToPlacementMargin(cosmetics.VisorPlacement, 78);
                LocalHatWidth = ToBodyCosmeticPlacementWidth(cosmetics.Hat, 78);
                LocalSkinWidth = ToSkinPlacementWidth(cosmetics.SkinPlacement, 78);
                LocalVisorWidth = ToPlacementWidth(cosmetics.VisorPlacement, 78);
            }

            UpdateOtherPlayers(state);
            if (IsImpostorRadioHeld)
            {
                SetImpostorRadioHeld(true);
            }

            RefreshObsOverlayService();
            _ = voiceSessionService.UpdateGameStateAsync(state, Settings);

            StatusMessage = $"Game state: {state.GameState}, lobby: {state.LobbyCode}";
        });
    }

    private void OnVoiceSessionStateChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshConnectionState();
            voicePlaybackService.ApplyMix(voiceSessionService.Peers);
            OnPropertyChanged(nameof(PeerConnectionStatus));
            OnPropertyChanged(nameof(CanChangeLobbySettings));
            OnPropertyChanged(nameof(LobbySettingsModeText));
            OnEffectiveLobbySettingsChanged();
            var effectiveState = voiceSessionService.CurrentState ?? lastAmongUsState;
            if (effectiveState is not null)
            {
                UpdateOtherPlayers(effectiveState);
            }
            RefreshObsOverlayService();
        });
    }

    private void OnVoiceSessionError(object? sender, string error)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = error;
        });
    }

    private void RefreshObsOverlayService()
    {
        if (!Settings.ObsOverlay)
        {
            obsOverlayService.Stop();
            obsOverlayService.UpdateSnapshot(ObsOverlaySnapshot.Empty);
            OnPropertyChanged(nameof(ObsOverlayUrl));
            OnPropertyChanged(nameof(ObsOverlayStatus));
            return;
        }

        try
        {
            if (!obsOverlayService.IsRunning)
            {
                obsOverlayService.Start(Settings.ObsSecret ?? string.Empty);
            }

            obsOverlayService.UpdateSnapshot(CreateObsOverlaySnapshot());
            OnPropertyChanged(nameof(ObsOverlayUrl));
            OnPropertyChanged(nameof(ObsOverlayStatus));
        }
        catch (Exception ex)
        {
            StatusMessage = $"OBSオーバーレイを開始できませんでした: {ex.Message}";
            OnPropertyChanged(nameof(ObsOverlayUrl));
            OnPropertyChanged(nameof(ObsOverlayStatus));
        }
    }

    private ObsOverlaySnapshot CreateObsOverlaySnapshot()
    {
        var visible = Settings.EnableOverlay &&
                      ShowVoiceView &&
                      (Settings.MeetingOverlay || !IsDiscussionView);

        return new ObsOverlaySnapshot(
            visible,
            new ObsOverlayLocalPlayer(
                LocalPlayerName,
                DisplayLobbyCode,
                BrushToHex(LocalPlayerBrush, "#50EF39"),
                BrushToHex(LocalPlayerShadowBrush, "#15A742"),
                IsMuted,
                IsDeafened),
            OtherPlayers.Select(static player => new ObsOverlayPlayer(
                player.Name,
                BrushToHex(player.MainBrush, "#50EF39"),
                BrushToHex(player.ShadowBrush, "#15A742"),
                player.IsTalking,
                player.IsUsingRadio,
                player.IsAudible,
                player.IsRemoteMuted || player.IsPlayerMuted)).ToArray());
    }

    private static string BrushToHex(Brush brush, string fallback)
    {
        if (brush is not SolidColorBrush solidColorBrush)
        {
            return fallback;
        }

        var color = solidColorBrush.Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void SetEffectiveLobbyBool(bool value, Func<LobbySettings, bool> getter, Action<LobbySettings, bool> setter, string propertyName)
    {
        if (!CanChangeLobbySettings || getter(Settings.LocalLobbySettings) == value)
        {
            return;
        }

        setter(Settings.LocalLobbySettings, value);
        OnPropertyChanged(propertyName);
        RefreshLobbySettingsFromSettings();
        QueueSettingsSave();
    }

    private void OnEffectiveLobbySettingsChanged()
    {
        OnPropertyChanged(nameof(EffectiveMaxDistance));
        OnPropertyChanged(nameof(EffectivePublicLobbyOn));
        OnPropertyChanged(nameof(EffectiveWallsBlockAudio));
        OnPropertyChanged(nameof(EffectiveVisionHearing));
        OnPropertyChanged(nameof(EffectiveHaunting));
        OnPropertyChanged(nameof(EffectiveThirdPartyHaunting));
        OnPropertyChanged(nameof(EffectiveVoiceEffectEnabled));
        OnPropertyChanged(nameof(EffectiveHearImpostorsInVents));
        OnPropertyChanged(nameof(EffectiveImpostorsHearImpostorsInVent));
        OnPropertyChanged(nameof(EffectiveCommsSabotage));
        OnPropertyChanged(nameof(EffectiveHearThroughCameras));
        OnPropertyChanged(nameof(EffectiveImpostorRadioEnabled));
        OnPropertyChanged(nameof(EffectiveDeadOnly));
        OnPropertyChanged(nameof(EffectiveMeetingGhostOnly));
    }

    private void OnPeerDataPayloadQueued(object? sender, PeerDataPayloadEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = $"peer data queued: {e.SocketId}";
        });
    }

    private void OnPeerAudioFrameQueued(object? sender, PeerAudioFrameEventArgs e)
    {
        queuedVoiceFrames++;
        if ((queuedVoiceFrames % 50) == 0)
        {
            Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(VoiceCaptureStatus)));
        }
    }

    private void OnPeerAudioFrameReceived(object? sender, PeerAudioFrameEventArgs e)
    {
        voicePlaybackService.SubmitFrame(e.SocketId, e.Frame);
        Application.Current.Dispatcher.Invoke(() =>
        {
            var peer = voiceSessionService.Peers.FirstOrDefault(peer => peer.SocketId == e.SocketId);
            if (peer is null || peer.ClientId <= 0)
            {
                return;
            }

            MarkRemoteTalking(peer.ClientId, e.Frame.Level);
        });
    }

    private void OnPublicLobbiesChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var currentModId = ModIdToProtocol(currentMod);
            PublicLobbies.Clear();
            foreach (var lobby in publicLobbyBrowserService.Lobbies
                         .Where(static lobby => lobby.IsPublic)
                         .OrderBy(static lobby => lobby.GameState == GameState.Lobby ? 0 : 1)
                         .ThenByDescending(static lobby => lobby.CurrentPlayers))
            {
                PublicLobbies.Add(new PublicLobbyViewModel(lobby, currentModId));
            }

            IsPublicLobbyBrowserLoading = false;
            PublicLobbyBrowserMessage = PublicLobbies.Count == 0
                ? "公開ロビーは見つかりませんでした。"
                : $"{PublicLobbies.Count} 件の公開ロビーを表示中です。";
        });
    }

    private void OnPublicLobbyBrowserError(object? sender, string error)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsPublicLobbyBrowserLoading = false;
            PublicLobbyBrowserMessage = error;
        });
    }

    private void UpdateOtherPlayers(AmongUsState state)
    {
        OtherPlayers.Clear();
        var peersByClientId = voiceSessionService.Peers.ToDictionary(static peer => peer.ClientId);
        var now = DateTimeOffset.UtcNow;

        foreach (var player in state.Players.Where(static player => !player.IsLocal && !player.Disconnected).OrderBy(static player => player.Id))
        {
            peersByClientId.TryGetValue(player.ClientId, out var peer);
            var hasRecentVoiceFrame = remoteTalkingUntil.TryGetValue(player.ClientId, out var activeUntil) && activeUntil > now;
            var colors = PlayerColorPalette.Get(player.ColorId, state.PlayerColors);
            var playerConfigKey = player.NameHash != 0 ? player.NameHash.ToString() : player.Name;
            var playerConfig = Settings.PlayerConfigMap.TryGetValue(playerConfigKey, out var config) ? config : new SocketConfig();
            OtherPlayers.Add(new PlayerTileViewModel(
                playerConfigKey,
                player.ClientId,
                player.Name,
                new SolidColorBrush(colors.Main),
                new SolidColorBrush(colors.Shadow),
                avatarImageService.GetPlayerImage(colors.Main, colors.Shadow),
                cosmeticCatalogService.GetImages(player, currentMod),
                player.IsDead,
                peer?.IsTalking == true || hasRecentVoiceFrame,
                peer?.IsUsingRadio == true,
                peer?.IsMuted == true,
                peer?.IsDeafened == true,
                peer?.Mix?.IsAudible != false,
                playerConfig,
                OnPlayerTileConfigChanged));
        }
    }

    private void MarkRemoteTalking(int clientId, double level)
    {
        if (level < 0.01)
        {
            return;
        }

        remoteTalkingUntil[clientId] = DateTimeOffset.UtcNow.AddMilliseconds(450);
        var tile = OtherPlayers.FirstOrDefault(player => player.ClientId == clientId);
        if (tile is null)
        {
            return;
        }

        tile.IsTalking = true;
        _ = Task.Delay(500).ContinueWith(_ =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (remoteTalkingUntil.TryGetValue(clientId, out var activeUntil) &&
                    activeUntil <= DateTimeOffset.UtcNow)
                {
                    remoteTalkingUntil.Remove(clientId);
                    var currentTile = OtherPlayers.FirstOrDefault(player => player.ClientId == clientId);
                    if (currentTile is not null)
                    {
                        currentTile.IsTalking = voiceSessionService.Peers.Any(peer => peer.ClientId == clientId && peer.IsTalking);
                    }

                    RefreshObsOverlayService();
                }
            });
        });
        RefreshObsOverlayService();
    }

    private void OnPlayerTileConfigChanged(PlayerTileViewModel player)
    {
        Settings.PlayerConfigMap[player.ConfigKey] = new SocketConfig
        {
            IsMuted = player.IsPlayerMuted,
            Volume = player.VolumePercent / 100d
        };

        _ = settingsService.SaveAsync(Settings);
        if (lastAmongUsState is not null)
        {
            _ = voiceSessionService.UpdateGameStateAsync(lastAmongUsState, Settings);
        }
    }

    private void OnAmongUsMemoryError(object? sender, string error)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = error;
        });
    }

    private static string ModIdToProtocol(AmongUsModType mod)
    {
        return mod switch
        {
            AmongUsModType.SuperNewRoles => "SUPER_NEW_ROLES",
            AmongUsModType.TownOfUsMira => "TOWN_OF_US_MIRA",
            AmongUsModType.TownOfUs => "TOWN_OF_US",
            AmongUsModType.TheOtherRoles => "THE_OTHER_ROLES",
            AmongUsModType.LasMonjas => "LAS_MONJAS",
            AmongUsModType.Other => "OTHER",
            _ => "NONE"
        };
    }

    private static string FormatModName(string mod)
    {
        return mod switch
        {
            "SUPER_NEW_ROLES" => "SuperNewRoles",
            "TOWN_OF_US_MIRA" => "Town of Us: Mira",
            "TOWN_OF_US" => "Town of Us: Reactivated",
            "THE_OTHER_ROLES" => "The Other Roles",
            "LAS_MONJAS" => "Las Monjas",
            "OTHER" => "Other",
            _ => "None"
        };
    }

    private static string FormatLanguageName(string language)
    {
        return language switch
        {
            "ja" => "日本語",
            "en" => "English",
            "es" => "Español",
            "pt_BR" => "Português",
            "fr" => "Français",
            "de" => "Deutsch",
            "ru" => "Русский",
            "zh_CN" => "简体中文",
            "ko" => "한국어",
            _ => language
        };
    }

    private static bool ValidateServerUrl(string url)
    {
        var normalized = NormalizeServerUrl(url);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (string.Equals(uri.Host, "discord.gg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/";
    }

    private static void OpenExternal(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string NormalizeServerUrl(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"https://{trimmed}";
        }

        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed[..^1] : trimmed;
    }

    private static string MaskLobbyCode(string lobbyCode)
    {
        if (string.IsNullOrWhiteSpace(lobbyCode) ||
            string.Equals(lobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            return lobbyCode;
        }

        return new string('*', lobbyCode.Length);
    }

    private static List<string> NormalizeServerUrls(IEnumerable<string> urls)
    {
        return urls
            .Select(NormalizeServerUrl)
            .Where(ValidateServerUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static class PlayerColorPalette
    {
        private static readonly (Color Main, Color Shadow)[] Colors =
        [
            (Color.FromRgb(0xC5, 0x11, 0x11), Color.FromRgb(0x7A, 0x08, 0x38)),
            (Color.FromRgb(0x13, 0x2E, 0xD1), Color.FromRgb(0x09, 0x15, 0x8E)),
            (Color.FromRgb(0x11, 0x7F, 0x2D), Color.FromRgb(0x0A, 0x4D, 0x2E)),
            (Color.FromRgb(0xED, 0x54, 0xBA), Color.FromRgb(0xAB, 0x2B, 0xAD)),
            (Color.FromRgb(0xEF, 0x7D, 0x0D), Color.FromRgb(0xB3, 0x3E, 0x15)),
            (Color.FromRgb(0xF5, 0xF5, 0x57), Color.FromRgb(0xC3, 0x88, 0x23)),
            (Color.FromRgb(0x3F, 0x47, 0x4E), Color.FromRgb(0x1E, 0x1F, 0x26)),
            (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x83, 0x94, 0xBF)),
            (Color.FromRgb(0x6B, 0x2F, 0xBB), Color.FromRgb(0x3B, 0x17, 0x7C)),
            (Color.FromRgb(0x71, 0x49, 0x1E), Color.FromRgb(0x5E, 0x26, 0x15)),
            (Color.FromRgb(0x38, 0xFE, 0xDC), Color.FromRgb(0x24, 0xA8, 0xBE)),
            (Color.FromRgb(0x50, 0xEF, 0x39), Color.FromRgb(0x15, 0xA7, 0x42))
        ];

        public static (Color Main, Color Shadow) Get(int colorId, IReadOnlyList<PlayerColorPair> dynamicColors)
        {
            if (colorId >= 0 && colorId < dynamicColors.Count)
            {
                return (FromGameColor(dynamicColors[colorId].Main), FromGameColor(dynamicColors[colorId].Shadow));
            }

            return colorId >= 0 && colorId < Colors.Length ? Colors[colorId] : Colors[11];
        }

        private static Color FromGameColor(uint color)
        {
            return Color.FromRgb(
                (byte)(color & 0x000000ff),
                (byte)((color & 0x0000ff00) >> 8),
                (byte)((color & 0x00ff0000) >> 16));
        }
    }

    private static Thickness ToSkinPlacementMargin(CosmeticPlacement placement, double baseSize)
    {
        return ToBodyCosmeticPlacementMargin(placement, baseSize);
    }

    private static double ToSkinPlacementWidth(CosmeticPlacement placement, double baseSize)
    {
        return ToBodyCosmeticPlacementWidth(placement, baseSize);
    }

    private static Thickness ToBodyCosmeticPlacementMargin(CosmeticPlacement placement, double baseSize)
    {
        if (placement.WidthPercent <= 0)
        {
            return new Thickness(baseSize * 0.20d, baseSize * 0.06d, 0, 0);
        }

        var rawLeft = (placement.LeftPercent / 100d) * baseSize;
        var rawTop = (placement.TopPercent / 100d) * baseSize;
        return new Thickness(
            rawLeft + (baseSize * 0.18d),
            Math.Max(baseSize * 0.04d, rawTop - (baseSize * 0.04d)),
            0,
            0);
    }

    private static Thickness ToPlacementMargin(CosmeticPlacement placement, double baseSize)
    {
        if (placement.WidthPercent <= 0)
        {
            return new Thickness(0);
        }

        return new Thickness(
            (placement.LeftPercent / 100d) * baseSize,
            (placement.TopPercent / 100d) * baseSize,
            0,
            0);
    }

    private static double ToPlacementWidth(CosmeticPlacement placement, double baseSize)
    {
        return placement.WidthPercent <= 0 ? baseSize : (placement.WidthPercent / 100d) * baseSize;
    }

    private static double ToBodyCosmeticPlacementWidth(CosmeticPlacement placement, double baseSize)
    {
        var width = placement.WidthPercent <= 0 ? baseSize * 2d : (placement.WidthPercent / 100d) * baseSize * 2d;
        return Math.Clamp(width, baseSize * 1.64d, baseSize * 2.24d);
    }

    public sealed class OptionViewModel
    {
        public OptionViewModel(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }

        public string Label { get; }
    }

    public sealed class PublicLobbyViewModel
    {
        public PublicLobbyViewModel(PublicLobby lobby, string currentMod)
        {
            Id = lobby.Id;
            Title = string.IsNullOrWhiteSpace(lobby.Title) ? "Untitled lobby" : lobby.Title;
            Host = string.IsNullOrWhiteSpace(lobby.Host) ? "-" : lobby.Host;
            PlayersText = $"{lobby.CurrentPlayers}/{lobby.MaxPlayers}";
            ModsText = FormatModName(lobby.Mods);
            LanguageText = FormatLanguageName(lobby.Language);
            StatusText = lobby.GameState == GameState.Lobby ? "Lobby" : "In game";
            CanShowCode = lobby.GameState == GameState.Lobby &&
                          lobby.CurrentPlayers < lobby.MaxPlayers &&
                          string.Equals(lobby.Mods, currentMod, StringComparison.OrdinalIgnoreCase);
        }

        public int Id { get; }

        public string Title { get; }

        public string Host { get; }

        public string PlayersText { get; }

        public string ModsText { get; }

        public string LanguageText { get; }

        public string StatusText { get; }

        public bool CanShowCode { get; }
    }

    public sealed class PlayerTileViewModel : ObservableObject
    {
        private readonly Action<PlayerTileViewModel> onConfigChanged;
        private bool isPlayerMuted;
        private bool isTalking;
        private double volumePercent;

        public PlayerTileViewModel(
            string configKey,
            int clientId,
            string name,
            Brush mainBrush,
            Brush shadowBrush,
            ImageSource avatarImage,
            CosmeticImageSet cosmetics,
            bool isDead,
            bool isTalking,
            bool isUsingRadio,
            bool isRemoteMuted,
            bool isRemoteDeafened,
            bool isAudible,
            SocketConfig config,
            Action<PlayerTileViewModel> onConfigChanged)
        {
            ConfigKey = configKey;
            ClientId = clientId;
            Name = name;
            MainBrush = mainBrush;
            ShadowBrush = shadowBrush;
            AvatarImage = avatarImage;
            this.onConfigChanged = onConfigChanged;
            isPlayerMuted = config.IsMuted;
            volumePercent = Math.Clamp(config.Volume * 100d, 0d, 200d);
            IsTalking = isTalking;
            IsUsingRadio = isUsingRadio;
            IsRemoteMuted = isRemoteMuted;
            IsRemoteDeafened = isRemoteDeafened;
            IsAudible = isAudible;
            HatFrontImage = cosmetics.HatFront;
            HatBackImage = cosmetics.HatBack;
            SkinImage = cosmetics.Skin;
            VisorImage = cosmetics.Visor;
            HatMargin = ToBodyCosmeticPlacementMargin(cosmetics.Hat, 38);
            SkinMargin = ToSkinPlacementMargin(cosmetics.SkinPlacement, 38);
            VisorMargin = ToPlacementMargin(cosmetics.VisorPlacement, 38);
            HatWidth = ToBodyCosmeticPlacementWidth(cosmetics.Hat, 38);
            SkinWidth = ToSkinPlacementWidth(cosmetics.SkinPlacement, 38);
            VisorWidth = ToPlacementWidth(cosmetics.VisorPlacement, 38);
            Opacity = isDead || !isAudible ? 0.45 : 1.0;
        }

        public string ConfigKey { get; }

        public int ClientId { get; }

        public string Name { get; }

        public Brush MainBrush { get; }

        public Brush ShadowBrush { get; }

        public ImageSource AvatarImage { get; }

        public bool IsTalking
        {
            get => isTalking;
            set
            {
                if (SetProperty(ref isTalking, value))
                {
                    OnPropertyChanged(nameof(RingBrush));
                    OnPropertyChanged(nameof(RingThickness));
                    OnPropertyChanged(nameof(RingGlowOpacity));
                }
            }
        }

        public bool IsUsingRadio { get; }

        public bool IsRemoteMuted { get; }

        public bool IsRemoteDeafened { get; }

        public bool IsAudible { get; }

        public bool IsPlayerMuted
        {
            get => isPlayerMuted;
            set
            {
                if (SetProperty(ref isPlayerMuted, value))
                {
                    onConfigChanged(this);
                    OnPropertyChanged(nameof(EffectiveVolumeText));
                }
            }
        }

        public double VolumePercent
        {
            get => volumePercent;
            set
            {
                var next = Math.Round(Math.Clamp(value, 0d, 200d));
                if (SetProperty(ref volumePercent, next))
                {
                    onConfigChanged(this);
                    OnPropertyChanged(nameof(EffectiveVolumeText));
                }
            }
        }

        public string EffectiveVolumeText => IsPlayerMuted ? "ミュート" : $"{VolumePercent:0}%";

        public Brush RingBrush => IsTalking
            ? new SolidColorBrush(Color.FromRgb(0x32, 0xFF, 0x7E))
            : new SolidColorBrush(Color.FromRgb(0x3C, 0x37, 0x46));

        public double RingThickness => IsTalking ? 3.2 : 1.2;

        public double RingGlowOpacity => IsTalking ? 0.85 : 0;

        public string HatFrontImage { get; }

        public string HatBackImage { get; }

        public string SkinImage { get; }

        public string VisorImage { get; }

        public Thickness HatMargin { get; }

        public Thickness SkinMargin { get; }

        public Thickness VisorMargin { get; }

        public double HatWidth { get; }

        public double SkinWidth { get; }

        public double VisorWidth { get; }

        public double Opacity { get; }
    }
}
