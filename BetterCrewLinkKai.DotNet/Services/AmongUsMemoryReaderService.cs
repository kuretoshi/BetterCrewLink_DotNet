using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class AmongUsMemoryReaderService : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const string BaseUrl = "https://raw.githubusercontent.com/OhMyGuus/BetterCrewlink-Offsets/main";

    private static readonly HttpClient HttpClient = new();
    private readonly object syncRoot = new();
    private CancellationTokenSource? cancellationTokenSource;
    private Task? monitorTask;
    private ReaderContext? context;
    private AmongUsProcessInfo? processInfo;
    private GameState lastGameState = GameState.Unknown;
    private string lastLobbyCode = "MENU";
    private string lastLocalPlayerName = string.Empty;
    private string lastLocalAppearanceId = string.Empty;
    private string lastPlayersSignature = string.Empty;
    private string lastSessionSignature = string.Empty;

    public event EventHandler<AmongUsState>? StateChanged;
    public event EventHandler<string>? Error;

    public void SetProcess(AmongUsProcessInfo? nextProcessInfo)
    {
        lock (syncRoot)
        {
            processInfo = nextProcessInfo;
            context?.Dispose();
            context = null;
            lastGameState = GameState.Unknown;
            lastLobbyCode = "MENU";
        }

        if (nextProcessInfo is null)
        {
            StateChanged?.Invoke(this, new AmongUsState { GameState = GameState.Menu, LobbyCode = "MENU" });
        }
    }

    public void Start()
    {
        if (monitorTask is { IsCompleted: false })
        {
            return;
        }

        cancellationTokenSource = new CancellationTokenSource();
        monitorTask = MonitorAsync(cancellationTokenSource.Token);
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        context?.Dispose();
        cancellationTokenSource?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = await TryReadStateAsync(cancellationToken);
                var localPlayerName = state?.Players.FirstOrDefault(static player => player.IsLocal)?.Name ?? string.Empty;
                var localAppearanceId = state?.Players.FirstOrDefault(static player => player.IsLocal)?.AppearanceId ?? string.Empty;
                var playersSignature = state is null ? string.Empty : BuildPlayersSignature(state.Players);
                var sessionSignature = state is null ? string.Empty : BuildSessionSignature(state);
                if (state is not null &&
                    (state.GameState != lastGameState ||
                     state.LobbyCode != lastLobbyCode ||
                     localPlayerName != lastLocalPlayerName ||
                     localAppearanceId != lastLocalAppearanceId ||
                     playersSignature != lastPlayersSignature ||
                     sessionSignature != lastSessionSignature))
                {
                    lastGameState = state.GameState;
                    lastLobbyCode = state.LobbyCode;
                    lastLocalPlayerName = localPlayerName;
                    lastLocalAppearanceId = localAppearanceId;
                    lastPlayersSignature = playersSignature;
                    lastSessionSignature = sessionSignature;
                    StateChanged?.Invoke(this, state);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex.Message);
            }

            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<AmongUsState?> TryReadStateAsync(CancellationToken cancellationToken)
    {
        AmongUsProcessInfo? currentProcess;
        ReaderContext? currentContext;

        lock (syncRoot)
        {
            currentProcess = processInfo;
            currentContext = context;
        }

        if (currentProcess is null)
        {
            return null;
        }

        if (currentContext is null)
        {
            currentContext = await InitializeContextAsync(currentProcess, cancellationToken);
            lock (syncRoot)
            {
                context = currentContext;
            }
        }

        var innerNetClient = currentContext.ReadPointer(currentContext.GameAssemblyBase, currentContext.Offsets.InnerNetClientBase);
        if (innerNetClient == 0)
        {
            return null;
        }

        var rawGameState = currentContext.ReadInt32(innerNetClient + currentContext.Offsets.InnerNetClientGameState);
        var gameState = rawGameState switch
        {
            0 => GameState.Menu,
            1 or 3 => GameState.Lobby,
            _ => ReadMeetingState(currentContext) < 4 ? GameState.Discussion : GameState.Tasks
        };

        var lobbyCodeInt = currentContext.ReadInt32(innerNetClient + currentContext.Offsets.InnerNetClientGameId);
        var isLocalGameCandidate = lobbyCodeInt == 32;
        var shouldReadSession = gameState != GameState.Menu || isLocalGameCandidate;

        var hostId = shouldReadSession ? currentContext.ReadUInt32(innerNetClient + currentContext.Offsets.InnerNetClientHostId) : 0;
        var clientId = shouldReadSession ? currentContext.ReadUInt32(innerNetClient + currentContext.Offsets.InnerNetClientClientId) : 0;
        currentContext.LoadPlayerColors();
        var players = shouldReadSession
            ? ReadPlayers(currentContext, unchecked((int)clientId), isLocalGameCandidate && gameState == GameState.Menu ? GameState.Lobby : gameState)
            : [];
        var isLocalGame = isLocalGameCandidate && players.Count > 0;
        var effectiveGameState = isLocalGame && gameState == GameState.Menu ? GameState.Lobby : gameState;
        var lobbyCode = effectiveGameState == GameState.Menu ? "MENU" : GameCodeCodec.IntToGameCode(lobbyCodeInt);

        if (isLocalGame)
        {
            var hostPlayer = players.FirstOrDefault(player => player.ClientId == unchecked((int)hostId));
            lobbyCode = hostPlayer is null
                ? "LOCAL"
                : Math.Abs(hostPlayer.NameHash % 99999).ToString("00000");
        }

        if (string.IsNullOrWhiteSpace(lobbyCode) && !isLocalGame)
        {
            lobbyCode = "MENU";
        }

        var (map, maxPlayers) = shouldReadSession ? currentContext.ReadGameOptions() : (MapType.Unknown, 15);
        var currentServer = shouldReadSession ? currentContext.ReadCurrentServer() : string.Empty;
        var localPlayer = players.FirstOrDefault(static player => player.IsLocal);
        var taskEnvironment = shouldReadSession && effectiveGameState == GameState.Tasks
            ? currentContext.ReadTaskEnvironment(map, localPlayer)
            : TaskEnvironment.Empty;

        return new AmongUsState
        {
            GameState = lobbyCode == "MENU" && !isLocalGame ? GameState.Menu : effectiveGameState,
            LobbyCodeInt = shouldReadSession ? lobbyCodeInt : -1,
            LobbyCode = lobbyCode,
            Players = players,
            PlayerColors = currentContext.PlayerColors.ToList(),
            HostId = unchecked((int)hostId),
            ClientId = unchecked((int)clientId),
            IsHost = hostId != 0 && hostId == clientId,
            CurrentServer = currentServer,
            MaxPlayers = maxPlayers,
            Map = map,
            CommsSabotaged = taskEnvironment.CommsSabotaged,
            CurrentCamera = taskEnvironment.CurrentCamera,
            ClosedDoors = taskEnvironment.ClosedDoors
        };
    }

    private static List<Player> ReadPlayers(ReaderContext context, int localClientId, GameState gameState)
    {
        var players = new List<Player>();
        var allPlayersPtr = context.ReadPointer(context.GameAssemblyBase, context.Offsets.AllPlayersPtr);
        var allPlayers = context.ReadPointer(allPlayersPtr, context.Offsets.AllPlayers);
        var playerCount = Math.Min(context.ReadInt32(allPlayersPtr, context.Offsets.PlayerCount), 40);
        var playerAddrPtr = allPlayers + context.Offsets.PlayerAddrPtr;

        for (var i = 0; i < playerCount; i++)
        {
            var resolved = context.ResolveAddress(playerAddrPtr, context.Offsets.PlayerOffsets);
            var playerDataAddress = resolved.Address + resolved.Last;
            playerAddrPtr += context.Is64Bit ? 8 : 4;

            if (playerDataAddress == 0 || gameState == GameState.Menu)
            {
                continue;
            }

            var player = ParsePlayer(context, playerDataAddress, localClientId);
            if (player is not null)
            {
                players.Add(player);
            }
        }

        return players;
    }

    private static string BuildPlayersSignature(IEnumerable<Player> players)
    {
        return string.Join('|', players
            .OrderBy(static player => player.ClientId)
            .Select(static player => $"{player.ClientId}:{player.Name}:{player.AppearanceId}:{player.IsDead}:{player.Disconnected}:{player.X:0.00}:{player.Y:0.00}"));
    }

    private static string BuildSessionSignature(AmongUsState state)
    {
        return $"{state.LobbyCodeInt}:{state.Map}:{state.MaxPlayers}:{state.CurrentServer}:{state.IsHost}:{state.CommsSabotaged}:{state.CurrentCamera}:{string.Join(',', state.ClosedDoors)}";
    }

    private static Player? ParsePlayer(ReaderContext context, long playerDataAddress, int localClientId)
    {
        var buffer = context.ReadBytes(playerDataAddress, context.Offsets.PlayerBufferLength);
        var data = context.ParsePlayerStruct(buffer);

        if (context.Is64Bit)
        {
            data.ObjectPtr = context.ReadPointer(playerDataAddress + context.GetPlayerStructOffset("objectPtr"));
            data.OutfitsPtr = context.ReadPointer(playerDataAddress + context.GetPlayerStructOffset("outfitsPtr"));
            data.TaskPtr = context.ReadPointer(playerDataAddress + context.GetPlayerStructOffset("taskPtr"));
            data.RolePtr = context.ReadPointer(playerDataAddress + context.GetPlayerStructOffset("rolePtr"));
        }

        if (data.ObjectPtr == 0)
        {
            return null;
        }

        var clientId = unchecked((int)context.ReadUInt32(data.ObjectPtr, context.Offsets.PlayerClientId));
        var isLocal = clientId == localClientId && data.Disconnected == 0;
        var positionOffsets = isLocal ? context.Offsets.PlayerLocalX : context.Offsets.PlayerRemoteX;
        var yOffsets = isLocal ? context.Offsets.PlayerLocalY : context.Offsets.PlayerRemoteY;

        var x = context.ReadFloat(data.ObjectPtr, positionOffsets);
        var y = context.ReadFloat(data.ObjectPtr, yOffsets);
        var currentOutfit = context.ReadUInt32(data.ObjectPtr, context.Offsets.PlayerCurrentOutfit);
        var isDummy = context.ReadByte(data.ObjectPtr, context.Offsets.PlayerIsDummy) > 0;
        var inVent = context.ReadByte(data.ObjectPtr, context.Offsets.PlayerInVent) > 0;

        var name = string.Empty;
        var color = unchecked((int)data.Color);
        var hatId = string.Empty;
        var skinId = string.Empty;
        var visorId = string.Empty;

        if (data.OutfitsPtr != 0)
        {
            context.ReadDictionary(data.OutfitsPtr, 6, (keyAddress, valueAddress, index) =>
            {
                var key = context.ReadInt32(keyAddress);
                var value = context.ReadPointer(valueAddress);
                if (value == 0)
                {
                    return;
                }

                if (key == 0 && index == 0)
                {
                    var namePtr = context.ReadPointer(value, context.Offsets.PlayerOutfitName);
                    name = context.ReadString(namePtr, 1000);
                    color = unchecked((int)context.ReadUInt32(value, context.Offsets.PlayerOutfitColorId));
                    hatId = context.ReadString(context.ReadPointer(value, context.Offsets.PlayerOutfitHatId));
                    skinId = context.ReadString(context.ReadPointer(value, context.Offsets.PlayerOutfitSkinId));
                    visorId = context.ReadString(context.ReadPointer(value, context.Offsets.PlayerOutfitVisorId));
                }
                else if (key == currentOutfit)
                {
                    color = unchecked((int)context.ReadUInt32(value, context.Offsets.PlayerOutfitColorId));
                    hatId = context.ReadString(context.ReadPointer(value, context.Offsets.PlayerOutfitHatId));
                    skinId = context.ReadString(context.ReadPointer(value, context.Offsets.PlayerOutfitSkinId));
                    visorId = context.ReadString(context.ReadPointer(value, context.Offsets.PlayerOutfitVisorId));
                }
            });
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "unknown";
        }

        var roleTeam = data.RolePtr == 0 ? 0 : context.ReadUInt32(data.RolePtr, context.Offsets.PlayerRoleTeam);

        return new Player
        {
            Id = unchecked((int)data.Id),
            ClientId = clientId,
            Name = StripRichText(name),
            NameHash = HashCode(StripRichText(name)),
            ColorId = color,
            HatId = hatId,
            SkinId = skinId,
            VisorId = visorId,
            AppearanceId = $"{color}|{hatId}|{skinId}|{visorId}",
            Disconnected = data.Disconnected != 0,
            IsImpostor = roleTeam == 1,
            IsThirdParty = roleTeam != 0 && roleTeam != 1,
            IsDead = data.Dead == 1,
            IsLocal = isLocal,
            X = Math.Round(x, 4),
            Y = Math.Round(y, 4),
            InVent = inVent
        };
    }

    private static string StripRichText(string text)
    {
        var start = text.IndexOf('<');
        while (start >= 0)
        {
            var end = text.IndexOf('>', start);
            if (end < 0)
            {
                break;
            }

            text = text.Remove(start, end - start + 1);
            start = text.IndexOf('<');
        }

        return text;
    }

    private static int HashCode(string text)
    {
        var hash = 0;
        foreach (var character in text)
        {
            hash = unchecked((31 * hash) + character);
        }

        return hash;
    }

    private static int ReadMeetingState(ReaderContext context)
    {
        var meetingHud = context.ReadPointer(context.GameAssemblyBase, context.Offsets.MeetingHud);
        var meetingHudCachePtr = meetingHud == 0
            ? 0
            : context.ReadPointer(meetingHud, context.Offsets.ObjectCachePtr);

        return meetingHudCachePtr == 0
            ? 4
            : context.ReadInt32(meetingHud, context.Offsets.MeetingHudState);
    }

    private static async Task<ReaderContext> InitializeContextAsync(AmongUsProcessInfo processInfo, CancellationToken cancellationToken)
    {
        using var process = Process.GetProcessById(processInfo.ProcessId);
        var gameAssembly = process.Modules
            .Cast<ProcessModule>()
            .First(module => string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));

        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Among Us のメモリを開けません。管理者権限が必要な可能性があります。");
        }

        var context = new ReaderContext(handle, gameAssembly.BaseAddress.ToInt64(), gameAssembly.ModuleMemorySize, processInfo.Is64Bit);
        try
        {
            var lookup = await FetchJsonAsync($"{BaseUrl}/lookup.json", cancellationToken);
            var broadcastSignature = GetSignature(lookup.RootElement.GetProperty("patterns").GetProperty(processInfo.Is64Bit ? "x64" : "x86").GetProperty("broadcastVersion"));
            var broadcastLocation = context.FindPattern(broadcastSignature, getLocation: true);
            var broadcastVersion = context.ReadInt32(context.GameAssemblyBase + broadcastLocation);

            var versions = lookup.RootElement.GetProperty("versions");
            var version = versions.TryGetProperty(broadcastVersion.ToString(), out var versionElement)
                ? versionElement
                : versions.GetProperty("default");
            var offsetFile = version.GetProperty("file").GetString() ?? throw new InvalidOperationException("Offset file was empty.");
            var offsetsJson = await FetchJsonAsync($"{BaseUrl}/offsets/{(processInfo.Is64Bit ? "x64" : "x86")}/{offsetFile}", cancellationToken);
            var offsets = BuildOffsets(context, offsetsJson.RootElement);

            context.Offsets = offsets;
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static ReaderOffsets BuildOffsets(ReaderContext context, JsonElement root)
    {
        var signatures = root.GetProperty("signatures");
        var innerNetClient = context.FindPattern(GetSignature(signatures.GetProperty("innerNetClient")));
        var meetingHud = context.FindPattern(GetSignature(signatures.GetProperty("meetingHud")));
        var gameData = context.FindPattern(GetSignature(signatures.GetProperty("gameData")));
        var palette = context.FindPattern(GetSignature(signatures.GetProperty("palette")));
        var playerControl = context.FindPattern(GetSignature(signatures.GetProperty("playerControl")));
        var shipStatus = signatures.TryGetProperty("shipStatus", out var shipStatusSignature)
            ? context.FindPattern(GetSignature(shipStatusSignature))
            : 0;
        var miniGame = signatures.TryGetProperty("miniGame", out var miniGameSignature)
            ? context.FindPattern(GetSignature(miniGameSignature))
            : 0;
        var gameOptionsBase = playerControl;
        if (root.TryGetProperty("newGameOptions", out var newGameOptionsElement) &&
            newGameOptionsElement.GetBoolean() &&
            signatures.TryGetProperty("gameOptionsManager", out var gameOptionsManagerSignature))
        {
            gameOptionsBase = context.FindPattern(GetSignature(gameOptionsManagerSignature));
        }

        var serverManager = signatures.TryGetProperty("serverManager", out var serverManagerSignature)
            ? context.FindPattern(GetSignature(serverManagerSignature))
            : 0;

        var player = root.GetProperty("player");

        return new ReaderOffsets
        {
            InnerNetClientBase = ReplaceFirst(GetIntArray(root.GetProperty("innerNetClient").GetProperty("base")), innerNetClient),
            InnerNetClientGameState = root.GetProperty("innerNetClient").GetProperty("gameState").GetInt32(),
            InnerNetClientGameId = root.GetProperty("innerNetClient").GetProperty("gameId").GetInt32(),
            InnerNetClientHostId = root.GetProperty("innerNetClient").GetProperty("hostId").GetInt32(),
            InnerNetClientClientId = root.GetProperty("innerNetClient").GetProperty("clientId").GetInt32(),
            MeetingHud = ReplaceFirst(GetIntArray(root.GetProperty("meetingHud")), meetingHud),
            ObjectCachePtr = GetIntArray(root.GetProperty("objectCachePtr")),
            MeetingHudState = GetIntArray(root.GetProperty("meetingHudState")),
            AllPlayersPtr = ReplaceFirst(GetIntArray(root.GetProperty("allPlayersPtr")), gameData),
            Palette = ReplaceFirst(GetIntArray(root.GetProperty("palette")), palette),
            PalettePlayerColor = GetIntArray(root.GetProperty("palette_playercolor")),
            PaletteShadowColor = GetIntArray(root.GetProperty("palette_shadowColor")),
            ShipStatus = TryReplaceFirst(root, "shipStatus", shipStatus),
            ShipStatusSystems = TryGetIntArray(root, "shipStatus_systems"),
            ShipStatusAllDoors = TryGetIntArray(root, "shipstatus_allDoors"),
            DoorIsOpen = root.TryGetProperty("door_isOpen", out var doorIsOpen) ? doorIsOpen.GetInt32() : 0,
            DeconDoorUpperOpen = TryGetIntArray(root, "deconDoorUpperOpen"),
            DeconDoorLowerOpen = TryGetIntArray(root, "deconDoorLowerOpen"),
            HqHudSystemTypeCompletedConsoles = TryGetIntArray(root, "hqHudSystemType_CompletedConsoles"),
            HudOverrideSystemTypeIsActive = TryGetIntArray(root, "HudOverrideSystemType_isActive"),
            MiniGame = TryReplaceFirst(root, "miniGame", miniGame),
            PlanetSurveillanceMinigameCurrentCamera = TryGetIntArray(root, "planetSurveillanceMinigame_currentCamera"),
            PlanetSurveillanceMinigameCamerasCount = TryGetIntArray(root, "planetSurveillanceMinigame_camarasCount"),
            SurveillanceMinigameFilteredRoomsCount = TryGetIntArray(root, "surveillanceMinigame_FilteredRoomsCount"),
            GameOptionsData = ReplaceFirst(GetIntArray(root.GetProperty("gameoptionsData")), gameOptionsBase),
            GameOptionsMapId = GetIntArray(root.GetProperty("gameOptions_MapId")),
            GameOptionsMaxPlayers = GetIntArray(root.GetProperty("gameOptions_MaxPLayers")),
            ServerManagerCurrentServer = ReplaceFirst(GetIntArray(root.GetProperty("serverManager_currentServer")), serverManager),
            AllPlayers = GetIntArray(root.GetProperty("allPlayers")),
            PlayerCount = GetIntArray(root.GetProperty("playerCount")),
            PlayerAddrPtr = root.GetProperty("playerAddrPtr").GetInt32(),
            PlayerBufferLength = player.GetProperty("bufferLength").GetInt32(),
            PlayerOffsets = GetIntArray(player.GetProperty("offsets")),
            PlayerStruct = BuildPlayerStruct(player.GetProperty("struct")),
            PlayerClientId = GetIntArray(player.GetProperty("clientId")),
            PlayerLocalX = GetIntArray(player.GetProperty("localX")),
            PlayerLocalY = GetIntArray(player.GetProperty("localY")),
            PlayerRemoteX = GetIntArray(player.GetProperty("remoteX")),
            PlayerRemoteY = GetIntArray(player.GetProperty("remoteY")),
            PlayerCurrentOutfit = GetIntArray(player.GetProperty("currentOutfit")),
            PlayerIsDummy = GetIntArray(player.GetProperty("isDummy")),
            PlayerInVent = GetIntArray(player.GetProperty("inVent")),
            PlayerRoleTeam = GetIntArray(player.GetProperty("roleTeam")),
            PlayerOutfitColorId = GetIntArray(player.GetProperty("outfit").GetProperty("colorId")),
            PlayerOutfitName = GetIntArray(player.GetProperty("outfit").GetProperty("playerName")),
            PlayerOutfitHatId = GetIntArray(player.GetProperty("outfit").GetProperty("hatId")),
            PlayerOutfitSkinId = GetIntArray(player.GetProperty("outfit").GetProperty("skinId")),
            PlayerOutfitVisorId = GetIntArray(player.GetProperty("outfit").GetProperty("visorId"))
        };
    }

    private static List<PlayerStructField> BuildPlayerStruct(JsonElement element)
    {
        var result = new List<PlayerStructField>();
        var offset = 0;

        foreach (var field in element.EnumerateArray())
        {
            var type = field.GetProperty("type").GetString() ?? string.Empty;
            var name = field.GetProperty("name").GetString() ?? string.Empty;
            var size = type switch
            {
                "SKIP" => field.TryGetProperty("skip", out var skip) ? skip.GetInt32() : 0,
                "BYTE" or "CHAR" => 1,
                "SHORT" or "USHORT" or "SHORT_BE" or "USHORT_BE" => 2,
                "INT" or "UINT" or "INT_BE" or "UINT_BE" or "FLOAT" => 4,
                _ => 4
            };

            result.Add(new PlayerStructField(name, type, offset, size));
            offset += size;
        }

        return result;
    }

    private static Signature GetSignature(JsonElement element)
    {
        return new Signature(
            element.GetProperty("sig").GetString() ?? string.Empty,
            element.GetProperty("patternOffset").GetInt32(),
            element.GetProperty("addressOffset").GetInt32());
    }

    private static int[] ReplaceFirst(int[] values, long firstValue)
    {
        var result = values.ToArray();
        if (result.Length == 0)
        {
            return result;
        }

        result[0] = unchecked((int)firstValue);
        return result;
    }

    private static int[] TryReplaceFirst(JsonElement root, string propertyName, long firstValue)
    {
        return root.TryGetProperty(propertyName, out var property) ? ReplaceFirst(GetIntArray(property), firstValue) : [];
    }

    private static int[] TryGetIntArray(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) ? GetIntArray(property) : [];
    }

    private static int[] GetIntArray(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(static value => value.GetInt32()).ToArray()
            : [element.GetInt32()];
    }

    private static async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        await using var stream = await HttpClient.GetStreamAsync(url, cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed record Signature(string Pattern, int PatternOffset, int AddressOffset);

    private sealed record TaskEnvironment(bool CommsSabotaged, CameraLocation CurrentCamera, List<int> ClosedDoors)
    {
        public static TaskEnvironment Empty { get; } = new(false, CameraLocation.None, []);
    }

    private sealed class ReaderOffsets
    {
        public int[] InnerNetClientBase { get; init; } = [];
        public int InnerNetClientGameState { get; init; }
        public int InnerNetClientGameId { get; init; }
        public int InnerNetClientHostId { get; init; }
        public int InnerNetClientClientId { get; init; }
        public int[] MeetingHud { get; init; } = [];
        public int[] ObjectCachePtr { get; init; } = [];
        public int[] MeetingHudState { get; init; } = [];
        public int[] AllPlayersPtr { get; init; } = [];
        public int[] Palette { get; init; } = [];
        public int[] PalettePlayerColor { get; init; } = [];
        public int[] PaletteShadowColor { get; init; } = [];
        public int[] ShipStatus { get; init; } = [];
        public int[] ShipStatusSystems { get; init; } = [];
        public int[] ShipStatusAllDoors { get; init; } = [];
        public int DoorIsOpen { get; init; }
        public int[] DeconDoorUpperOpen { get; init; } = [];
        public int[] DeconDoorLowerOpen { get; init; } = [];
        public int[] HqHudSystemTypeCompletedConsoles { get; init; } = [];
        public int[] HudOverrideSystemTypeIsActive { get; init; } = [];
        public int[] MiniGame { get; init; } = [];
        public int[] PlanetSurveillanceMinigameCurrentCamera { get; init; } = [];
        public int[] PlanetSurveillanceMinigameCamerasCount { get; init; } = [];
        public int[] SurveillanceMinigameFilteredRoomsCount { get; init; } = [];
        public int[] GameOptionsData { get; init; } = [];
        public int[] GameOptionsMapId { get; init; } = [];
        public int[] GameOptionsMaxPlayers { get; init; } = [];
        public int[] ServerManagerCurrentServer { get; init; } = [];
        public int[] AllPlayers { get; init; } = [];
        public int[] PlayerCount { get; init; } = [];
        public int PlayerAddrPtr { get; init; }
        public int PlayerBufferLength { get; init; }
        public int[] PlayerOffsets { get; init; } = [];
        public List<PlayerStructField> PlayerStruct { get; init; } = [];
        public int[] PlayerClientId { get; init; } = [];
        public int[] PlayerLocalX { get; init; } = [];
        public int[] PlayerLocalY { get; init; } = [];
        public int[] PlayerRemoteX { get; init; } = [];
        public int[] PlayerRemoteY { get; init; } = [];
        public int[] PlayerCurrentOutfit { get; init; } = [];
        public int[] PlayerIsDummy { get; init; } = [];
        public int[] PlayerInVent { get; init; } = [];
        public int[] PlayerRoleTeam { get; init; } = [];
        public int[] PlayerOutfitColorId { get; init; } = [];
        public int[] PlayerOutfitName { get; init; } = [];
        public int[] PlayerOutfitHatId { get; init; } = [];
        public int[] PlayerOutfitSkinId { get; init; } = [];
        public int[] PlayerOutfitVisorId { get; init; } = [];
    }

    private sealed record PlayerStructField(string Name, string Type, int Offset, int Size);

    private sealed class PlayerData
    {
        public uint Id { get; set; }
        public uint Color { get; set; }
        public uint Disconnected { get; set; }
        public byte Dead { get; set; }
        public long ObjectPtr { get; set; }
        public long OutfitsPtr { get; set; }
        public long TaskPtr { get; set; }
        public long RolePtr { get; set; }
    }

    private sealed class ReaderContext : IDisposable
    {
        private byte[]? moduleBytes;

        public ReaderContext(IntPtr handle, long gameAssemblyBase, int gameAssemblySize, bool is64Bit)
        {
            Handle = handle;
            GameAssemblyBase = gameAssemblyBase;
            GameAssemblySize = gameAssemblySize;
            Is64Bit = is64Bit;
        }

        public IntPtr Handle { get; }
        public long GameAssemblyBase { get; }
        public int GameAssemblySize { get; }
        public bool Is64Bit { get; }
        public ReaderOffsets Offsets { get; set; } = new();
        public List<PlayerColorPair> PlayerColors { get; private set; } = [];

        public int ReadInt32(long address) => BitConverter.ToInt32(ReadBytes(address, 4));

        public uint ReadUInt32(long address) => BitConverter.ToUInt32(ReadBytes(address, 4));

        public byte ReadByte(long address, IReadOnlyList<int> offsets)
        {
            var resolved = ResolveAddress(address, offsets);
            return resolved.Address == 0 ? (byte)0 : ReadBytes(resolved.Address + resolved.Last, 1)[0];
        }

        public float ReadFloat(long address, IReadOnlyList<int> offsets)
        {
            var resolved = ResolveAddress(address, offsets);
            return resolved.Address == 0 ? 0 : BitConverter.ToSingle(ReadBytes(resolved.Address + resolved.Last, 4));
        }

        public long ReadPointer(long address, IReadOnlyList<int> offsets)
        {
            var resolved = ResolveAddress(address, offsets);
            return resolved.Address == 0 ? 0 : ReadPointer(resolved.Address + resolved.Last);
        }

        public uint ReadUInt32(long address, IReadOnlyList<int> offsets)
        {
            var resolved = ResolveAddress(address, offsets);
            return resolved.Address == 0 ? 0 : ReadUInt32(resolved.Address + resolved.Last);
        }

        public int ReadInt32(long address, IReadOnlyList<int> offsets)
        {
            var resolved = ResolveAddress(address, offsets);
            return resolved.Address == 0 ? 0 : ReadInt32(resolved.Address + resolved.Last);
        }

        public long FindPattern(Signature signature, bool getLocation = false)
        {
            moduleBytes ??= ReadModuleBytes();
            var pattern = ParsePattern(signature.Pattern);

            for (var i = 0; i <= moduleBytes.Length - pattern.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (pattern[j].HasValue && moduleBytes[i + j] != pattern[j]!.Value)
                    {
                        matched = false;
                        break;
                    }
                }

                if (!matched)
                {
                    continue;
                }

                var instructionLocation = i + signature.PatternOffset;
                if (getLocation)
                {
                    return instructionLocation + signature.AddressOffset;
                }

                var offsetAddress = ReadInt32(GameAssemblyBase + instructionLocation);
                return Is64Bit
                    ? offsetAddress + instructionLocation + signature.AddressOffset
                    : offsetAddress - GameAssemblyBase;
            }

            throw new InvalidOperationException($"Pattern not found: {signature.Pattern}");
        }

        public int GetPlayerStructOffset(string name)
        {
            return Offsets.PlayerStruct.First(field => field.Name == name).Offset;
        }

        public PlayerData ParsePlayerStruct(byte[] buffer)
        {
            var data = new PlayerData();
            foreach (var field in Offsets.PlayerStruct)
            {
                if (field.Type == "SKIP" || field.Offset >= buffer.Length)
                {
                    continue;
                }

                switch (field.Name)
                {
                    case "id":
                        data.Id = ReadUInt32FromBuffer(buffer, field.Offset);
                        break;
                    case "color":
                        data.Color = ReadUInt32FromBuffer(buffer, field.Offset);
                        break;
                    case "disconnected":
                        data.Disconnected = ReadUInt32FromBuffer(buffer, field.Offset);
                        break;
                    case "dead":
                        data.Dead = buffer[field.Offset];
                        break;
                    case "objectPtr":
                        data.ObjectPtr = ReadUInt32FromBuffer(buffer, field.Offset);
                        break;
                    case "outfitsPtr":
                        data.OutfitsPtr = ReadUInt32FromBuffer(buffer, field.Offset);
                        break;
                    case "taskPtr":
                        data.TaskPtr = ReadUInt32FromBuffer(buffer, field.Offset);
                        break;
                    case "rolePtr":
                        data.RolePtr = ReadUInt32FromBuffer(buffer, field.Offset);
                        break;
                }
            }

            return data;
        }

        public void LoadPlayerColors()
        {
            if (PlayerColors.Count > 0)
            {
                return;
            }

            try
            {
                var palettePtr = ReadPointer(GameAssemblyBase, Offsets.Palette);
                var playerColorsPtr = ReadPointer(palettePtr + Offsets.PalettePlayerColor[0]);
                var shadowColorsPtr = ReadPointer(palettePtr + Offsets.PaletteShadowColor[0]);
                var colorLength = ReadInt32(shadowColorsPtr, Offsets.PlayerCount);

                if (colorLength <= 0 || colorLength > 300)
                {
                    return;
                }

                var colors = new List<PlayerColorPair>();
                for (var i = 0; i < colorLength; i++)
                {
                    colors.Add(new PlayerColorPair
                    {
                        Main = ReadUInt32(playerColorsPtr + Offsets.PlayerAddrPtr + (i * 4)),
                        Shadow = ReadUInt32(shadowColorsPtr + Offsets.PlayerAddrPtr + (i * 4))
                    });
                }

                PlayerColors = colors;
            }
            catch
            {
                PlayerColors = [];
            }
        }

        public (MapType Map, int MaxPlayers) ReadGameOptions()
        {
            try
            {
                var gameOptionsPtr = ReadPointer(GameAssemblyBase, Offsets.GameOptionsData);
                if (gameOptionsPtr == 0)
                {
                    return (MapType.Unknown, 15);
                }

                var map = (MapType)ReadByte(gameOptionsPtr, Offsets.GameOptionsMapId);
                var maxPlayers = ReadByte(gameOptionsPtr, Offsets.GameOptionsMaxPlayers);
                return (Enum.IsDefined(typeof(MapType), map) ? map : MapType.Unknown, maxPlayers <= 0 ? 15 : maxPlayers);
            }
            catch
            {
                return (MapType.Unknown, 15);
            }
        }

        public string ReadCurrentServer()
        {
            try
            {
                if (Offsets.ServerManagerCurrentServer.Length == 0 || Offsets.ServerManagerCurrentServer[0] == 0)
                {
                    return string.Empty;
                }

                var currentServer = ReadPointer(GameAssemblyBase, Offsets.ServerManagerCurrentServer);
                return ReadString(currentServer);
            }
            catch
            {
                return string.Empty;
            }
        }

        public TaskEnvironment ReadTaskEnvironment(MapType map, Player? localPlayer)
        {
            var closedDoors = new List<int>();
            var commsSabotaged = false;
            var currentCamera = CameraLocation.None;

            try
            {
                var shipPtr = Offsets.ShipStatus.Length == 0 ? 0 : ReadPointer(GameAssemblyBase, Offsets.ShipStatus);
                if (shipPtr == 0)
                {
                    return TaskEnvironment.Empty;
                }

                var systemsPtr = Offsets.ShipStatusSystems.Length == 0 ? 0 : ReadPointer(shipPtr, Offsets.ShipStatusSystems);
                if (systemsPtr != 0)
                {
                    ReadDictionary(systemsPtr, 47, (keyAddress, valueAddress, _) =>
                    {
                        var key = ReadInt32(keyAddress);
                        var value = ReadPointer(valueAddress);
                        if (value == 0)
                        {
                            return;
                        }

                        if (key == 14)
                        {
                            commsSabotaged = ReadCommsSabotage(value, map);
                        }
                        else if (key == 18 && map == MapType.MiraHq)
                        {
                            var lowerDoorOpen = Offsets.DeconDoorLowerOpen.Length > 0 && ReadInt32(value, Offsets.DeconDoorLowerOpen) != 0;
                            var upperDoorOpen = Offsets.DeconDoorUpperOpen.Length > 0 && ReadInt32(value, Offsets.DeconDoorUpperOpen) != 0;
                            if (!lowerDoorOpen)
                            {
                                closedDoors.Add(0);
                            }

                            if (!upperDoorOpen)
                            {
                                closedDoors.Add(1);
                            }
                        }
                    });
                }

                currentCamera = ReadCurrentCamera(map, localPlayer);

                if (map != MapType.MiraHq)
                {
                    ReadClosedDoors(shipPtr, closedDoors);
                }
            }
            catch
            {
                return new TaskEnvironment(commsSabotaged, currentCamera, closedDoors);
            }

            return new TaskEnvironment(commsSabotaged, currentCamera, closedDoors);
        }

        private bool ReadCommsSabotage(long systemValuePtr, MapType map)
        {
            try
            {
                return map switch
                {
                    MapType.Airship or MapType.Polus or MapType.TheSkeld or MapType.Submerged =>
                        Offsets.HudOverrideSystemTypeIsActive.Length > 0 &&
                        ReadUInt32(systemValuePtr, Offsets.HudOverrideSystemTypeIsActive) == 1,
                    MapType.Fungle or MapType.MiraHq =>
                        Offsets.HqHudSystemTypeCompletedConsoles.Length > 0 &&
                        ReadUInt32(systemValuePtr, Offsets.HqHudSystemTypeCompletedConsoles) < 2,
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        private CameraLocation ReadCurrentCamera(MapType map, Player? localPlayer)
        {
            try
            {
                if (Offsets.MiniGame.Length == 0)
                {
                    return CameraLocation.None;
                }

                var minigamePtr = ReadPointer(GameAssemblyBase, Offsets.MiniGame);
                var minigameCachePtr = minigamePtr == 0 || Offsets.ObjectCachePtr.Length == 0
                    ? 0
                    : ReadPointer(minigamePtr, Offsets.ObjectCachePtr);
                if (minigameCachePtr == 0 || localPlayer is null)
                {
                    return CameraLocation.None;
                }

                if (map is MapType.Polus or MapType.Airship &&
                    Offsets.PlanetSurveillanceMinigameCurrentCamera.Length > 0 &&
                    Offsets.PlanetSurveillanceMinigameCamerasCount.Length > 0)
                {
                    var currentCameraId = ReadUInt32(minigamePtr, Offsets.PlanetSurveillanceMinigameCurrentCamera);
                    var camerasCount = ReadUInt32(minigamePtr, Offsets.PlanetSurveillanceMinigameCamerasCount);
                    return currentCameraId <= 5 && camerasCount == 6 ? (CameraLocation)currentCameraId : CameraLocation.None;
                }

                if (map == MapType.TheSkeld && Offsets.SurveillanceMinigameFilteredRoomsCount.Length > 0)
                {
                    var roomCount = ReadUInt32(minigamePtr, Offsets.SurveillanceMinigameFilteredRoomsCount);
                    var dx = localPlayer.X - -12.9364;
                    var dy = localPlayer.Y - -2.7928;
                    return roomCount == 4 && Math.Sqrt((dx * dx) + (dy * dy)) < 0.6
                        ? CameraLocation.Skeld
                        : CameraLocation.None;
                }
            }
            catch
            {
                return CameraLocation.None;
            }

            return CameraLocation.None;
        }

        private void ReadClosedDoors(long shipPtr, List<int> closedDoors)
        {
            try
            {
                if (Offsets.ShipStatusAllDoors.Length == 0 || Offsets.PlayerCount.Length == 0 || Offsets.DoorIsOpen == 0)
                {
                    return;
                }

                var allDoors = ReadPointer(shipPtr, Offsets.ShipStatusAllDoors);
                if (allDoors == 0)
                {
                    return;
                }

                var doorCount = Math.Min(ReadInt32(allDoors, Offsets.PlayerCount), 16);
                for (var doorIndex = 0; doorIndex < doorCount; doorIndex++)
                {
                    var door = ReadPointer(allDoors + Offsets.PlayerAddrPtr + (doorIndex * (Is64Bit ? 8 : 4)));
                    if (door == 0)
                    {
                        continue;
                    }

                    var doorOpen = ReadInt32(door + Offsets.DoorIsOpen) == 1;
                    if (!doorOpen)
                    {
                        closedDoors.Add(doorIndex);
                    }
                }
            }
            catch
            {
                // Doors are optional for voice mixing; keep the rest of the state readable.
            }
        }

        public void ReadDictionary(long address, int maxLength, Action<long, long, int> callback)
        {
            var entries = ReadPointer(address + (Is64Bit ? 0x18 : 0x0c));
            var length = Math.Min((int)ReadUInt32(address + (Is64Bit ? 0x20 : 0x10)), maxLength);

            for (var i = 0; i < length; i++)
            {
                var offset = entries + ((Is64Bit ? 0x20 : 0x10) + (i * (Is64Bit ? 0x18 : 0x10)));
                callback(offset, offset + (Is64Bit ? 0x10 : 0x0c), i);
            }
        }

        public string ReadString(long address, int maxLength = 50)
        {
            try
            {
                if (address == 0)
                {
                    return string.Empty;
                }

                var length = Math.Clamp(ReadInt32(address + (Is64Bit ? 0x10 : 0x08)), 0, maxLength);
                if (length <= 0)
                {
                    return string.Empty;
                }

                return System.Text.Encoding.Unicode.GetString(ReadBytes(address + (Is64Bit ? 0x14 : 0x0c), length * 2)).Replace("\0", string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                CloseHandle(Handle);
            }
        }

        private static byte?[] ParsePattern(string pattern)
        {
            return pattern
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(static part => part.Contains('?') ? (byte?)null : Convert.ToByte(part, 16))
                .ToArray();
        }

        public long ReadPointer(long address)
        {
            return Is64Bit ? BitConverter.ToInt64(ReadBytes(address, 8)) : BitConverter.ToUInt32(ReadBytes(address, 4));
        }

        public (long Address, int Last) ResolveAddress(long address, IReadOnlyList<int> offsets)
        {
            for (var i = 0; i < offsets.Count - 1; i++)
            {
                address = ReadPointer(address + offsets[i]);
                if (address == 0)
                {
                    break;
                }
            }

            return (address, offsets.Count > 0 ? offsets[^1] : 0);
        }

        public byte[] ReadBytes(long address, int size)
        {
            var buffer = new byte[size];
            if (!ReadProcessMemory(Handle, new IntPtr(address), buffer, size, out var bytesRead) || bytesRead.ToInt64() <= 0)
            {
                throw new InvalidOperationException($"Failed to read Among Us memory at 0x{address:X}.");
            }

            return buffer;
        }

        private static uint ReadUInt32FromBuffer(byte[] buffer, int offset)
        {
            return offset + 4 <= buffer.Length ? BitConverter.ToUInt32(buffer, offset) : 0;
        }

        private byte[] ReadModuleBytes()
        {
            var buffer = new byte[GameAssemblySize];
            const int chunkSize = 64 * 1024;

            for (var offset = 0; offset < GameAssemblySize; offset += chunkSize)
            {
                var size = Math.Min(chunkSize, GameAssemblySize - offset);
                var chunk = new byte[size];
                if (!ReadProcessMemory(Handle, new IntPtr(GameAssemblyBase + offset), chunk, size, out var bytesRead) ||
                    bytesRead.ToInt64() <= 0)
                {
                    continue;
                }

                Array.Copy(chunk, 0, buffer, offset, (int)bytesRead);
            }

            return buffer;
        }
    }
}
