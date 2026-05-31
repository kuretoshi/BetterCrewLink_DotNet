using System.Collections.ObjectModel;
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
    private readonly CosmeticCatalogService cosmeticCatalogService;
    private readonly AvatarImageService avatarImageService;
    private readonly AmongUsProcessService amongUsProcessService;
    private readonly AmongUsMemoryReaderService amongUsMemoryReaderService;
    private AppSettings settings = new();
    private string lobbyCode = string.Empty;
    private string statusMessage = "Among Us を起動してロビーコードを入力してください。";
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

    public MainWindowViewModel()
        : this(new SettingsService(), new GameLaunchService(), new VoiceSessionService(), new AudioDeviceService(), new CosmeticCatalogService(), new AvatarImageService(), new AmongUsProcessService(), new AmongUsMemoryReaderService())
    {
    }

    public MainWindowViewModel(
        SettingsService settingsService,
        GameLaunchService gameLaunchService,
        VoiceSessionService voiceSessionService,
        AudioDeviceService audioDeviceService,
        CosmeticCatalogService cosmeticCatalogService,
        AvatarImageService avatarImageService,
        AmongUsProcessService amongUsProcessService,
        AmongUsMemoryReaderService amongUsMemoryReaderService)
    {
        this.settingsService = settingsService;
        this.gameLaunchService = gameLaunchService;
        this.voiceSessionService = voiceSessionService;
        this.audioDeviceService = audioDeviceService;
        this.cosmeticCatalogService = cosmeticCatalogService;
        this.avatarImageService = avatarImageService;
        this.amongUsProcessService = amongUsProcessService;
        this.amongUsMemoryReaderService = amongUsMemoryReaderService;
        this.amongUsProcessService.ProcessChanged += OnAmongUsProcessChanged;
        this.amongUsMemoryReaderService.StateChanged += OnAmongUsStateChanged;
        this.amongUsMemoryReaderService.Error += OnAmongUsMemoryError;
        this.audioDeviceService.MicrophoneLevelChanged += OnMicrophoneLevelChanged;
        this.voiceSessionService.StateChanged += OnVoiceSessionStateChanged;
        this.voiceSessionService.Error += OnVoiceSessionError;

        Platforms = new ObservableCollection<GamePlatform>(
            Enum.GetValues<GamePlatform>().Where(static platform => platform != GamePlatform.Custom));
        PushToTalkModes = new ObservableCollection<PushToTalkMode>(Enum.GetValues<PushToTalkMode>());
        Microphones = [];
        Speakers = [];
        OtherPlayers = [];

        LoadCommand = new RelayCommand(_ => LoadAsync());
        SaveCommand = new RelayCommand(_ => SaveAsync());
        LaunchGameCommand = new RelayCommand(_ => LaunchGameAsync());
        ToggleConnectionCommand = new RelayCommand(_ => ToggleConnectionAsync());
        RefreshAudioDevicesCommand = new RelayCommand(_ =>
        {
            RefreshAudioDevices();
            RestartMicrophoneMonitor();
            return Task.CompletedTask;
        });
        TestSpeakerCommand = new RelayCommand(_ => TestSpeakerAsync());
        TestVoiceEffectCommand = new RelayCommand(_ =>
        {
            ToggleVoiceEffectTest();
            return Task.CompletedTask;
        });
        ToggleSettingsCommand = new RelayCommand(_ =>
        {
            IsSettingsOpen = !IsSettingsOpen;
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
        CloseCommand = new RelayCommand(_ =>
        {
            Application.Current.Shutdown();
            return Task.CompletedTask;
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
            }
        }
    }

    public ObservableCollection<GamePlatform> Platforms { get; }

    public ObservableCollection<PushToTalkMode> PushToTalkModes { get; }

    public ObservableCollection<AudioDeviceInfo> Microphones { get; }

    public ObservableCollection<AudioDeviceInfo> Speakers { get; }

    public ObservableCollection<PlayerTileViewModel> OtherPlayers { get; }

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

    public string CurrentLobbyCode
    {
        get => currentLobbyCode;
        private set => SetProperty(ref currentLobbyCode, value);
    }

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

    public ICommand LoadCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand LaunchGameCommand { get; }

    public ICommand ToggleConnectionCommand { get; }

    public ICommand RefreshAudioDevicesCommand { get; }

    public ICommand TestSpeakerCommand { get; }

    public ICommand TestVoiceEffectCommand { get; }

    public ICommand ToggleSettingsCommand { get; }

    public ICommand ToggleLaunchPlatformMenuCommand { get; }

    public ICommand SelectLaunchPlatformCommand { get; }

    public ICommand CloseCommand { get; }

    private async Task LoadAsync()
    {
        Settings = await settingsService.LoadAsync();
        StatusMessage = $"設定を読み込みました: {settingsService.SettingsPath}";
        await cosmeticCatalogService.InitializeAsync();
        RefreshAudioDevices();
        RestartMicrophoneMonitor();
        _ = voiceSessionService.ConnectAsync(Settings.ServerUrl, "MENU");
        amongUsProcessService.Start();
        amongUsMemoryReaderService.Start();
    }

    private async Task SaveAsync()
    {
        await settingsService.SaveAsync(Settings);
        StatusMessage = "設定を保存しました。";
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
            voiceSessionService.Disconnect();
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
            voiceSessionService.Disconnect();
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

    private void ToggleVoiceEffectTest()
    {
        try
        {
            if (IsVoiceEffectTestRunning)
            {
                audioDeviceService.StopVoiceEffectTest();
                IsVoiceEffectTestRunning = false;
                RestartMicrophoneMonitor();
                return;
            }

            audioDeviceService.StopMicrophoneMonitor();
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
            StatusMessage = $"ボイスエフェクトテストに失敗しました: {ex.Message}";
        }
    }

    private void OnMicrophoneLevelChanged(object? sender, double level)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            MicrophoneLevel = level;
        });
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
            IsInGameSession = state.GameState is GameState.Lobby or GameState.Tasks or GameState.Discussion;
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
                LocalHatMargin = ToPlacementMargin(cosmetics.Hat, 78);
                LocalSkinMargin = ToInnerPlacementMargin(cosmetics.SkinPlacement, 78);
                LocalVisorMargin = ToPlacementMargin(cosmetics.VisorPlacement, 78);
                LocalHatWidth = ToPlacementWidth(cosmetics.Hat, 78);
                LocalSkinWidth = ToPlacementWidth(cosmetics.SkinPlacement, 78);
                LocalVisorWidth = ToPlacementWidth(cosmetics.VisorPlacement, 78);
            }

            UpdateOtherPlayers(state);
            _ = voiceSessionService.UpdateGameStateAsync(state, Settings);

            StatusMessage = $"Game state: {state.GameState}, lobby: {state.LobbyCode}";
        });
    }

    private void OnVoiceSessionStateChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshConnectionState();
        });
    }

    private void OnVoiceSessionError(object? sender, string error)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = error;
        });
    }

    private void UpdateOtherPlayers(AmongUsState state)
    {
        OtherPlayers.Clear();

        foreach (var player in state.Players.Where(static player => !player.IsLocal && !player.Disconnected).OrderBy(static player => player.Id))
        {
            var colors = PlayerColorPalette.Get(player.ColorId, state.PlayerColors);
            OtherPlayers.Add(new PlayerTileViewModel(
                player.Name,
                new SolidColorBrush(colors.Main),
                new SolidColorBrush(colors.Shadow),
                avatarImageService.GetPlayerImage(colors.Main, colors.Shadow),
                cosmeticCatalogService.GetImages(player, currentMod),
                player.IsDead));
        }
    }

    private void OnAmongUsMemoryError(object? sender, string error)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = error;
        });
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

    private static Thickness ToPlacementMargin(CosmeticPlacement placement, double baseSize)
    {
        return ToPlacementMargin(placement, baseSize, false);
    }

    private static Thickness ToInnerPlacementMargin(CosmeticPlacement placement, double baseSize)
    {
        return ToPlacementMargin(placement, baseSize, true);
    }

    private static Thickness ToPlacementMargin(CosmeticPlacement placement, double baseSize, bool innerLayer)
    {
        if (placement.WidthPercent <= 0)
        {
            return new Thickness(0);
        }

        var border = Math.Max(2, baseSize / 40d);
        const double paddingLeft = -7;
        var layerOffset = innerLayer ? -border : 0;
        return new Thickness(
            ((placement.LeftPercent / 100d) * baseSize) + (border / 2d) + paddingLeft + layerOffset,
            (((22 + placement.TopPercent) / 100d) * baseSize) + layerOffset,
            0,
            0);
    }

    private static double ToPlacementWidth(CosmeticPlacement placement, double baseSize)
    {
        return placement.WidthPercent <= 0 ? baseSize : (placement.WidthPercent / 100d) * baseSize;
    }

    public sealed class PlayerTileViewModel
    {
        public PlayerTileViewModel(string name, Brush mainBrush, Brush shadowBrush, ImageSource avatarImage, CosmeticImageSet cosmetics, bool isDead)
        {
            Name = name;
            MainBrush = mainBrush;
            ShadowBrush = shadowBrush;
            AvatarImage = avatarImage;
            HatFrontImage = cosmetics.HatFront;
            HatBackImage = cosmetics.HatBack;
            SkinImage = cosmetics.Skin;
            VisorImage = cosmetics.Visor;
            HatMargin = ToPlacementMargin(cosmetics.Hat, 38);
            SkinMargin = ToInnerPlacementMargin(cosmetics.SkinPlacement, 38);
            VisorMargin = ToPlacementMargin(cosmetics.VisorPlacement, 38);
            HatWidth = ToPlacementWidth(cosmetics.Hat, 38);
            SkinWidth = ToPlacementWidth(cosmetics.SkinPlacement, 38);
            VisorWidth = ToPlacementWidth(cosmetics.VisorPlacement, 38);
            Opacity = isDead ? 0.45 : 1.0;
        }

        public string Name { get; }

        public Brush MainBrush { get; }

        public Brush ShadowBrush { get; }

        public ImageSource AvatarImage { get; }

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
