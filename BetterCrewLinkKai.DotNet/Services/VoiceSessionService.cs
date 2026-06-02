using System.Text.Json;
using BetterCrewLinkKai.DotNet.Models;
using NAudio.Wave;
using SocketIOClient;
using SocketIOClient.Transport;

namespace BetterCrewLinkKai.DotNet.Services;

public enum VoiceSessionState
{
    Disconnected,
    Connecting,
    Connected
}

public sealed class VoiceClient
{
    public string SocketId { get; init; } = string.Empty;

    public int PlayerId { get; init; }

    public int ClientId { get; init; }

    public bool IsTalking { get; set; }

    public bool IsUsingRadio { get; set; }

    public bool IsMuted { get; set; }

    public bool IsDeafened { get; set; }
}

public sealed class VoicePeerState
{
    public string SocketId { get; init; } = string.Empty;

    public int PlayerId { get; init; }

    public int ClientId { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsTalking { get; init; }

    public bool IsUsingRadio { get; init; }

    public bool IsMuted { get; init; }

    public bool IsDeafened { get; init; }

    public bool IsConnected { get; init; }

    public VoicePeerConnectionState PeerConnectionState { get; init; } = VoicePeerConnectionState.Disconnected;

    public PlayerVoiceMix? Mix { get; init; }
}

public sealed class VoiceSessionService : IDisposable
{
    private readonly VoiceMixService voiceMixService = new();
    private readonly PeerConnectionService peerConnectionService = new();
    private SocketIOClient.SocketIO? socket;
    private AmongUsState? lastState;
    private AppSettings? lastSettings;
    private string currentLobby = "MENU";
    private string lastLobbyPresenceKey = string.Empty;
    private string lastMobileHostKey = string.Empty;
    private string lastMutePresenceKey = string.Empty;
    private bool localMuted;
    private bool localDeafened;
    private bool disposed;

    public VoiceSessionState State { get; private set; } = VoiceSessionState.Disconnected;

    public string StatusText { get; private set; } = "Disconnected";

    public int ServerHostId { get; private set; }

    public Dictionary<string, VoiceClient> SocketClients { get; } = [];

    public IReadOnlyDictionary<int, PlayerVoiceMix> VoiceMix { get; private set; } = new Dictionary<int, PlayerVoiceMix>();

    public IReadOnlyList<VoicePeerState> Peers { get; private set; } = [];

    public AmongUsState? CurrentState => lastState;

    public IReadOnlyDictionary<string, VoicePeerConnection> PeerConnections => peerConnectionService.Peers;

    public bool UseNatFix => peerConnectionService.UseNatFix;

    public IReadOnlyList<string> IceServers => peerConnectionService.IceServers;

    public int ImpostorRadioClientId { get; private set; } = -1;

    public LobbySettings? HostLobbySettings { get; private set; }

    public event EventHandler? StateChanged;

    public event EventHandler<string>? Error;

    public event EventHandler<PeerDataPayloadEventArgs>? PeerDataPayloadQueued;

    public event EventHandler<PeerAudioFrameEventArgs>? PeerAudioFrameQueued;

    public event EventHandler<PeerAudioFrameEventArgs>? PeerAudioFrameReceived;

    public VoiceSessionService()
    {
        peerConnectionService.PeersChanged += (_, _) =>
        {
            RebuildPeers();
            NotifyStateChanged();
        };
        peerConnectionService.DataPayloadQueued += (_, args) => PeerDataPayloadQueued?.Invoke(this, args);
        peerConnectionService.AudioFrameQueued += (_, args) => PeerAudioFrameQueued?.Invoke(this, args);
    }

    public async Task ConnectAsync(string serverUrl, string lobbyCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ArgumentException("Server URL is empty.", nameof(serverUrl));
        }

        State = VoiceSessionState.Connecting;
        StatusText = "Connecting";
        NotifyStateChanged();

        await DisconnectSocketAsync();

        socket = new SocketIOClient.SocketIO(NormalizeServerUrl(serverUrl), new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            Reconnection = true
        });

        RegisterSocketHandlers(socket);
        await socket.ConnectAsync(cancellationToken);

        State = VoiceSessionState.Connected;
        StatusText = $"Connected to {serverUrl}";
        currentLobby = string.IsNullOrWhiteSpace(lobbyCode) ? "MENU" : lobbyCode;
        lastLobbyPresenceKey = string.Empty;
        NotifyStateChanged();
    }

    public async Task UpdateGameStateAsync(AmongUsState state, AppSettings? settings = null, CancellationToken cancellationToken = default)
    {
        lastState = state;
        if (settings is not null)
        {
            lastSettings = settings;
        }

        if (state.IsHost && settings is not null)
        {
            HostLobbySettings = CloneLobbySettings(settings.LocalLobbySettings);
        }

        if (settings is not null)
        {
            peerConnectionService.ConfigureNatFix(settings.NatFix);
            VoiceMix = voiceMixService.Calculate(state, CreateEffectiveSettings(settings, state), ImpostorRadioClientId);
            RebuildPeers(state);
            NotifyStateChanged();
        }

        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        var localPlayer = state.Players.FirstOrDefault(static player => player.IsLocal);
        if (localPlayer is null)
        {
            return;
        }

        await socket.EmitAsync("id", localPlayer.Id, state.ClientId, cancellationToken);

        if (state.GameState == GameState.Menu || string.Equals(state.LobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            if (currentLobby != "MENU")
            {
                await socket.EmitAsync("leave", cancellationToken);
                currentLobby = "MENU";
                lastLobbyPresenceKey = string.Empty;
                lastMobileHostKey = string.Empty;
                lastMutePresenceKey = string.Empty;
                SocketClients.Clear();
                peerConnectionService.Clear();
                Peers = [];
                HostLobbySettings = null;
                lastSettings = null;
                StatusText = "Left voice lobby";
                NotifyStateChanged();
            }

            return;
        }

        if (currentLobby != state.LobbyCode)
        {
            await socket.EmitAsync("leave", cancellationToken);
            await socket.EmitAsync("join", state.LobbyCode, localPlayer.Id, state.ClientId, state.IsHost, cancellationToken);
            currentLobby = state.LobbyCode;
            lastLobbyPresenceKey = string.Empty;
            lastMobileHostKey = string.Empty;
            lastMutePresenceKey = string.Empty;
            StatusText = $"Joined voice lobby: {state.LobbyCode}";
            NotifyStateChanged();
        }

        await EmitLobbyPresenceAsync(state, settings, cancellationToken);
        await EmitMobileHostInfoAsync(state, settings, cancellationToken);
        await EmitLocalMuteStateIfNeededAsync(cancellationToken);
    }

    public async Task EmitVadAsync(bool talking, CancellationToken cancellationToken = default)
    {
        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        await socket.EmitAsync("VAD", talking, cancellationToken);
    }

    public void SetLocalImpostorRadio(bool isUsingRadio)
    {
        var localPlayer = lastState?.Players.FirstOrDefault(static player => player.IsLocal);
        ImpostorRadioClientId = isUsingRadio && localPlayer is not null ? localPlayer.ClientId : -1;
        BroadcastPeerDataAsync(new
        {
            impostorRadio = ImpostorRadioClientId != -1
        });
        NotifyStateChanged();
    }

    public void SetLocalMuteState(bool muted, bool deafened)
    {
        localMuted = muted;
        localDeafened = deafened;
        lastMutePresenceKey = string.Empty;
        BroadcastPeerDataAsync(new
        {
            muted,
            deafened
        });
        NotifyStateChanged();
    }

    public void ApplyPeerData(string peerSocketId, string json)
    {
        if (!SocketClients.TryGetValue(peerSocketId, out var client))
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("impostorRadio", out var impostorRadioElement))
        {
            var isUsingRadio = impostorRadioElement.GetBoolean();
            foreach (var socketClient in SocketClients.Values)
            {
                socketClient.IsUsingRadio = socketClient.SocketId == peerSocketId && isUsingRadio;
            }

            RecalculateRadioClient();
        }

        if (root.TryGetProperty("muted", out var mutedElement) &&
            mutedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            client.IsMuted = mutedElement.GetBoolean();
            if (client.IsMuted)
            {
                client.IsTalking = false;
            }
        }

        if (root.TryGetProperty("deafened", out var deafenedElement) &&
            deafenedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            client.IsDeafened = deafenedElement.GetBoolean();
            if (client.IsDeafened)
            {
                client.IsMuted = true;
                client.IsTalking = false;
            }
        }

        if (TryReadLobbySettings(root, out var lobbySettings) && IsHostClient(client))
        {
            HostLobbySettings = lobbySettings;
        }

        if (lastState is not null)
        {
            RebuildPeers(lastState);
        }

        NotifyStateChanged();
    }

    public string CreateLobbySettingsPayload(AppSettings settings)
    {
        return JsonSerializer.Serialize(ToOriginalSettingsShape(settings));
    }

    public void BroadcastLobbySettings(AppSettings settings)
    {
        BroadcastPeerDataAsync(ToOriginalSettingsShape(settings));
    }

    public void QueueLocalAudioFrame(VoiceAudioFrame frame)
    {
        if (State != VoiceSessionState.Connected || !frame.IsTransmitting)
        {
            return;
        }

        peerConnectionService.BroadcastAudioFrame(frame);
        _ = EmitPeerAudioFrameAsync(frame);
    }

    public void SubmitRemoteAudioFrame(string socketId, VoiceAudioFrame frame)
    {
        if (SocketClients.ContainsKey(socketId))
        {
            PeerAudioFrameReceived?.Invoke(this, new PeerAudioFrameEventArgs(socketId, frame));
        }
    }

    public async Task DisconnectAsync()
    {
        await DisconnectSocketAsync();
        State = VoiceSessionState.Disconnected;
        StatusText = "Disconnected";
        currentLobby = "MENU";
        lastLobbyPresenceKey = string.Empty;
        lastMobileHostKey = string.Empty;
        lastMutePresenceKey = string.Empty;
        SocketClients.Clear();
        peerConnectionService.Clear();
        VoiceMix = new Dictionary<int, PlayerVoiceMix>();
        Peers = [];
        ImpostorRadioClientId = -1;
        HostLobbySettings = null;
        lastSettings = null;
        NotifyStateChanged();
    }

    public void Disconnect()
    {
        Task.Run(DisconnectAsync).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Disconnect();
    }

    private void RegisterSocketHandlers(SocketIOClient.SocketIO nextSocket)
    {
        nextSocket.OnConnected += (_, _) =>
        {
            State = VoiceSessionState.Connected;
            StatusText = "Voice server connected";
            NotifyStateChanged();
        };

        nextSocket.OnDisconnected += (_, reason) =>
        {
            State = VoiceSessionState.Disconnected;
            StatusText = $"Disconnected: {reason}";
            currentLobby = "MENU";
            lastLobbyPresenceKey = string.Empty;
            lastMobileHostKey = string.Empty;
            lastMutePresenceKey = string.Empty;
            SocketClients.Clear();
            peerConnectionService.Clear();
            Peers = [];
            ImpostorRadioClientId = -1;
            HostLobbySettings = null;
            lastSettings = null;
            NotifyStateChanged();
        };

        nextSocket.OnError += (_, error) =>
        {
            StatusText = $"Socket.IO error: {error}";
            Error?.Invoke(this, StatusText);
            NotifyStateChanged();
        };

        nextSocket.On("setHost", response =>
        {
            ServerHostId = response.GetValue<int>();
            NotifyStateChanged();
        });

        nextSocket.On("setClient", response =>
        {
            var socketId = response.GetValue<string>(0);
            var client = ReadClient(response.GetValue<JsonElement>(1), socketId);
            if (!string.IsNullOrWhiteSpace(socketId))
            {
                SocketClients[socketId] = client;
                peerConnectionService.EnsurePeer(socketId, client, initiator: false);
                RebuildPeers();
                _ = EmitLocalMuteStateIfNeededAsync();
                NotifyStateChanged();
            }
        });

        nextSocket.On("setClients", response =>
        {
            SocketClients.Clear();
            var clients = response.GetValue<JsonElement>();
            foreach (var clientProperty in clients.EnumerateObject())
            {
                SocketClients[clientProperty.Name] = ReadClient(clientProperty.Value, clientProperty.Name);
            }

            foreach (var client in SocketClients.Values)
            {
                peerConnectionService.EnsurePeer(client.SocketId, client, initiator: false);
            }

            RebuildPeers();
            _ = EmitLocalMuteStateIfNeededAsync();
            NotifyStateChanged();
        });

        nextSocket.On("join", response =>
        {
            var socketId = response.GetValue<string>(0);
            var client = ReadClient(response.GetValue<JsonElement>(1), socketId);
            if (!string.IsNullOrWhiteSpace(socketId))
            {
                SocketClients[socketId] = client;
                peerConnectionService.EnsurePeer(socketId, client, initiator: true);
                RebuildPeers();
                _ = EmitLocalMuteStateIfNeededAsync();
                NotifyStateChanged();
            }
        });

        nextSocket.On("leave", response =>
        {
            var socketId = response.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(socketId) && SocketClients.Remove(socketId))
            {
                peerConnectionService.RemovePeer(socketId);
                RecalculateRadioClient();
                RebuildPeers();
                NotifyStateChanged();
            }
        });

        nextSocket.On("VAD", response =>
        {
            var data = response.GetValue<JsonElement>();
            if (data.TryGetProperty("socketId", out var socketIdElement) &&
            SocketClients.TryGetValue(socketIdElement.GetString() ?? string.Empty, out var client))
        {
            client.IsTalking = data.TryGetProperty("activity", out var activity) &&
                               activity.GetBoolean() &&
                               !client.IsMuted &&
                               !client.IsDeafened;
            RebuildPeers();
            NotifyStateChanged();
        }
        });

        nextSocket.On("signal", response =>
        {
            var signal = response.GetValue<JsonElement>();
            HandleSignal(signal);
        });
    }

    private void HandleSignal(JsonElement signalEnvelope)
    {
        if (!signalEnvelope.TryGetProperty("data", out var data))
        {
            return;
        }

        if (!signalEnvelope.TryGetProperty("from", out var fromElement))
        {
            return;
        }

        var from = fromElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(from))
        {
            return;
        }

        if (!SocketClients.TryGetValue(from, out var client) &&
            signalEnvelope.TryGetProperty("client", out var clientElement))
        {
            client = ReadClient(clientElement, from);
            SocketClients[from] = client;
        }

        if (client is null)
        {
            Error?.Invoke(this, $"Signal from unknown socket: {from}");
            return;
        }

        if (data.TryGetProperty("type", out _))
        {
            peerConnectionService.ApplySignal(from, client, data);
            return;
        }

        if (data.TryGetProperty("mobilePlayerInfo", out var mobilePlayerInfo))
        {
            ApplyMobilePlayerInfo(client, mobilePlayerInfo);
            return;
        }

        if (data.TryGetProperty("voiceFrame", out var voiceFrameElement))
        {
            ApplyPeerAudioFrame(from, client, voiceFrameElement);
            return;
        }

        ApplyPeerData(from, data.GetRawText());
    }

    private void ApplyPeerAudioFrame(string socketId, VoiceClient client, JsonElement voiceFrameElement)
    {
        var frame = ReadVoiceFrame(voiceFrameElement);
        if (frame is null || client.IsMuted || client.IsDeafened)
        {
            return;
        }

        client.IsTalking = frame.Level > 0.01;
        peerConnectionService.MarkConnected(socketId);
        SubmitRemoteAudioFrame(socketId, frame);
        RebuildPeers();
        NotifyStateChanged();
    }

    private void ApplyMobilePlayerInfo(VoiceClient sender, JsonElement mobilePlayerInfo)
    {
        if (!IsHostClient(sender) || lastState is null)
        {
            return;
        }

        var nextState = CloneState(lastState);
        nextState.HostId = GetInt(mobilePlayerInfo, "hostId", nextState.HostId);
        nextState.ClientId = GetInt(mobilePlayerInfo, "clientId", nextState.ClientId);
        nextState.LobbyCode = GetString(mobilePlayerInfo, "lobbyCode", nextState.LobbyCode);
        nextState.GameState = ReadGameState(GetString(mobilePlayerInfo, "gameState", nextState.GameState.ToString()), nextState.GameState);
        nextState.Map = ReadMap(GetString(mobilePlayerInfo, "map", nextState.Map.ToString()), nextState.Map);

        if (mobilePlayerInfo.TryGetProperty("players", out var playersElement) &&
            playersElement.ValueKind == JsonValueKind.Array)
        {
            var localPlayer = nextState.Players.FirstOrDefault(static player => player.IsLocal);
            var remotePlayers = playersElement
                .EnumerateArray()
                .Select(ReadMobilePlayer)
                .Where(static player => player is not null)
                .Select(static player => player!)
                .Where(player => localPlayer is null || player.ClientId != localPlayer.ClientId)
                .ToList();

            nextState.Players = localPlayer is null ? remotePlayers : [localPlayer, .. remotePlayers];
        }

        lastState = nextState;
        if (lastSettings is not null)
        {
            VoiceMix = voiceMixService.Calculate(nextState, CreateEffectiveSettings(lastSettings, nextState), ImpostorRadioClientId);
        }

        RebuildPeers(nextState);
        NotifyStateChanged();
    }

    private void BroadcastPeerDataAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        peerConnectionService.BroadcastDataPayload(json);
        _ = EmitPeerDataAsync(payload);
    }

    private async Task EmitPeerDataAsync(object payload, CancellationToken cancellationToken = default)
    {
        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        var localPlayer = lastState?.Players.FirstOrDefault(static player => player.IsLocal);
        var client = localPlayer is null
            ? null
            : new
            {
                playerId = localPlayer.Id,
                clientId = localPlayer.ClientId
            };

        foreach (var peer in peerConnectionService.Peers.Values)
        {
            await socket.EmitAsync("signal", new
            {
                to = peer.SocketId,
                client,
                data = payload
            }, cancellationToken);
        }
    }

    private async Task EmitPeerAudioFrameAsync(VoiceAudioFrame frame, CancellationToken cancellationToken = default)
    {
        if (socket is null || State != VoiceSessionState.Connected || lastState is null)
        {
            return;
        }

        var localPlayer = lastState.Players.FirstOrDefault(static player => player.IsLocal);
        var client = localPlayer is null
            ? null
            : new
            {
                playerId = localPlayer.Id,
                clientId = localPlayer.ClientId
            };
        var payload = ToVoiceFramePayload(frame);

        foreach (var peer in peerConnectionService.Peers.Values)
        {
            await socket.EmitAsync("signal", new
            {
                to = peer.SocketId,
                client,
                data = new
                {
                    voiceFrame = payload
                }
            }, cancellationToken);
        }
    }

    private async Task EmitLocalMuteStateIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        var peerIds = string.Join(',', peerConnectionService.Peers.Keys.Order(StringComparer.Ordinal));
        var key = $"{currentLobby}|{localMuted}|{localDeafened}|{peerIds}";
        if (key == lastMutePresenceKey)
        {
            return;
        }

        lastMutePresenceKey = key;
        await EmitPeerDataAsync(new
        {
            muted = localMuted,
            deafened = localDeafened
        }, cancellationToken);
    }

    private async Task EmitLobbyPresenceAsync(AmongUsState state, AppSettings? settings, CancellationToken cancellationToken)
    {
        if (socket is null || settings is null || string.Equals(state.LobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var lobby = CreateEffectiveSettings(settings, state).LocalLobbySettings;
        var key = string.Join('|',
            state.LobbyCode,
            state.IsHost,
            state.MaxPlayers,
            state.Map,
            state.CurrentServer,
            settings.NatFix,
            lobby.PublicLobbyOn,
            lobby.PublicLobbyTitle,
            lobby.PublicLobbyLanguage,
            lobby.PublicLobbyMods);

        if (key == lastLobbyPresenceKey)
        {
            return;
        }

        lastLobbyPresenceKey = key;
        await socket.EmitAsync("lobby", state.LobbyCode, new
        {
            players = state.Players.Count(static player => !player.Disconnected),
            max = state.MaxPlayers,
            map = state.Map.ToString(),
            server = state.CurrentServer,
            title = lobby.PublicLobbyTitle,
            language = lobby.PublicLobbyLanguage,
            mods = lobby.PublicLobbyMods,
            natFix = settings.NatFix,
            isPublic = lobby.PublicLobbyOn
        }, cancellationToken);

        if (state.IsHost)
        {
            await socket.EmitAsync("setHost", state.LobbyCode, state.ClientId, cancellationToken);
        }
    }

    private async Task EmitMobileHostInfoAsync(AmongUsState state, AppSettings? settings, CancellationToken cancellationToken)
    {
        if (socket is null ||
            settings is null ||
            !settings.MobileHost ||
            !state.IsHost ||
            string.Equals(state.LobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var activePlayers = state.Players
            .Where(static player => !player.Disconnected)
            .OrderBy(static player => player.Id)
            .ToArray();
        var key = string.Join('|',
            state.LobbyCode,
            state.ClientId,
            state.HostId,
            state.GameState,
            string.Join(',', activePlayers.Select(static player => $"{player.Id}:{player.ClientId}:{player.NameHash}:{player.ColorId}:{player.IsDead}:{player.IsImpostor}:{player.X:0.00}:{player.Y:0.00}:{player.InVent}")));

        if (key == lastMobileHostKey)
        {
            return;
        }

        lastMobileHostKey = key;
        await EmitPeerDataAsync(new
        {
            mobilePlayerInfo = new
            {
                lobbyCode = state.LobbyCode,
                hostId = state.HostId,
                clientId = state.ClientId,
                gameState = state.GameState.ToString(),
                map = state.Map.ToString(),
                players = activePlayers.Select(static player => new
                {
                    playerId = player.Id,
                    clientId = player.ClientId,
                    name = player.Name,
                    nameHash = player.NameHash,
                    colorId = player.ColorId,
                    isDead = player.IsDead,
                    isImpostor = player.IsImpostor,
                    isThirdParty = player.IsThirdParty,
                    inVent = player.InVent,
                    x = player.X,
                    y = player.Y
                }).ToArray()
            }
        }, cancellationToken);
    }

    private async Task DisconnectSocketAsync()
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            await socket.EmitAsync("leave");
            await socket.DisconnectAsync();
        }
        catch
        {
            // Best effort cleanup.
        }
        finally
        {
            socket.Dispose();
            socket = null;
        }
    }

    private void RebuildPeers()
    {
        if (lastState is not null)
        {
            RebuildPeers(lastState);
        }
    }

    private void RebuildPeers(AmongUsState state)
    {
        var playersByClientId = state.Players
            .Where(static player => !player.IsLocal)
            .GroupBy(static player => player.ClientId)
            .ToDictionary(static group => group.Key, static group => group.First());
        var mixByClientId = VoiceMix.Values.ToDictionary(static mix => mix.ClientId);
        var peerConnections = PeerConnections;

        Peers = SocketClients.Values
            .Select(client =>
            {
                playersByClientId.TryGetValue(client.ClientId, out var player);
                mixByClientId.TryGetValue(client.ClientId, out var mix);
                peerConnections.TryGetValue(client.SocketId, out var connection);
                return new VoicePeerState
                {
                    SocketId = client.SocketId,
                    PlayerId = client.PlayerId,
                    ClientId = client.ClientId,
                    Name = player?.Name ?? string.Empty,
                    IsTalking = client.IsTalking,
                    IsUsingRadio = client.IsUsingRadio,
                    IsMuted = client.IsMuted,
                    IsDeafened = client.IsDeafened,
                    IsConnected = player is not null,
                    PeerConnectionState = connection?.State ?? VoicePeerConnectionState.Disconnected,
                    Mix = mix
                };
            })
            .OrderByDescending(static peer => peer.IsConnected)
            .ThenBy(static peer => peer.ClientId)
            .ToList();
    }

    private void RecalculateRadioClient()
    {
        ImpostorRadioClientId = SocketClients.Values.FirstOrDefault(static client => client.IsUsingRadio)?.ClientId ?? -1;
    }

    private bool IsHostClient(VoiceClient client)
    {
        return lastState is not null && (client.ClientId == lastState.HostId || client.ClientId == ServerHostId);
    }

    private AppSettings CreateEffectiveSettings(AppSettings settings, AmongUsState state)
    {
        if (state.IsHost || HostLobbySettings is null)
        {
            return settings;
        }

        return new AppSettings
        {
            AlwaysOnTop = settings.AlwaysOnTop,
            Language = settings.Language,
            Microphone = settings.Microphone,
            Speaker = settings.Speaker,
            PushToTalkMode = settings.PushToTalkMode,
            ServerUrl = settings.ServerUrl,
            ServerUrls = settings.ServerUrls,
            PushToTalkShortcut = settings.PushToTalkShortcut,
            DeafenShortcut = settings.DeafenShortcut,
            MuteShortcut = settings.MuteShortcut,
            ImpostorRadioShortcut = settings.ImpostorRadioShortcut,
            HideCode = settings.HideCode,
            NatFix = settings.NatFix,
            CompactOverlay = settings.CompactOverlay,
            OverlayPosition = settings.OverlayPosition,
            EnableOverlay = settings.EnableOverlay,
            MeetingOverlay = settings.MeetingOverlay,
            LocalLobbySettings = CloneLobbySettings(HostLobbySettings),
            GhostVolumeAsImpostor = settings.GhostVolumeAsImpostor,
            CrewVolumeAsGhost = settings.CrewVolumeAsGhost,
            MasterVolume = settings.MasterVolume,
            MicrophoneGain = settings.MicrophoneGain,
            MicrophoneGainEnabled = settings.MicrophoneGainEnabled,
            VoiceEffectStrength = settings.VoiceEffectStrength,
            MicSensitivity = settings.MicSensitivity,
            MicSensitivityEnabled = settings.MicSensitivityEnabled,
            MobileHost = settings.MobileHost,
            VadEnabled = settings.VadEnabled,
            HardwareAcceleration = settings.HardwareAcceleration,
            EchoCancellation = settings.EchoCancellation,
            NoiseSuppression = settings.NoiseSuppression,
            OldSampleDebug = settings.OldSampleDebug,
            EnableSpatialAudio = settings.EnableSpatialAudio,
            PlayerConfigMap = settings.PlayerConfigMap,
            ObsOverlay = settings.ObsOverlay,
            ObsSecret = settings.ObsSecret,
            LaunchPlatform = settings.LaunchPlatform,
            CustomPlatforms = settings.CustomPlatforms
        };
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizeServerUrl(string serverUrl)
    {
        var trimmed = serverUrl.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://{trimmed}";
    }

    private static VoiceClient ReadClient(JsonElement element, string socketId)
    {
        return new VoiceClient
        {
            SocketId = socketId,
            PlayerId = element.TryGetProperty("playerId", out var playerId) ? playerId.GetInt32() : 0,
            ClientId = element.TryGetProperty("clientId", out var clientId) ? clientId.GetInt32() : 0
        };
    }

    private static AmongUsState CloneState(AmongUsState state)
    {
        return new AmongUsState
        {
            GameState = state.GameState,
            LobbyCodeInt = state.LobbyCodeInt,
            LobbyCode = state.LobbyCode,
            Players = state.Players.Select(static player => new Player
            {
                Id = player.Id,
                ClientId = player.ClientId,
                Name = player.Name,
                NameHash = player.NameHash,
                ColorId = player.ColorId,
                HatId = player.HatId,
                SkinId = player.SkinId,
                VisorId = player.VisorId,
                AppearanceId = player.AppearanceId,
                Disconnected = player.Disconnected,
                IsImpostor = player.IsImpostor,
                IsThirdParty = player.IsThirdParty,
                IsDead = player.IsDead,
                IsLocal = player.IsLocal,
                X = player.X,
                Y = player.Y,
                InVent = player.InVent
            }).ToList(),
            PlayerColors = state.PlayerColors.Select(static color => new PlayerColorPair
            {
                Main = color.Main,
                Shadow = color.Shadow
            }).ToList(),
            IsHost = state.IsHost,
            ClientId = state.ClientId,
            HostId = state.HostId,
            CommsSabotaged = state.CommsSabotaged,
            CurrentServer = state.CurrentServer,
            MaxPlayers = state.MaxPlayers,
            Map = state.Map,
            CurrentCamera = state.CurrentCamera,
            ClosedDoors = [.. state.ClosedDoors]
        };
    }

    private static Player? ReadMobilePlayer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new Player
        {
            Id = GetInt(element, "playerId"),
            ClientId = GetInt(element, "clientId"),
            Name = GetString(element, "name"),
            NameHash = GetInt(element, "nameHash"),
            ColorId = GetInt(element, "colorId"),
            IsDead = GetBool(element, "isDead"),
            IsImpostor = GetBool(element, "isImpostor"),
            IsThirdParty = GetBool(element, "isThirdParty"),
            InVent = GetBool(element, "inVent"),
            X = GetDouble(element, "x"),
            Y = GetDouble(element, "y")
        };
    }

    private static GameState ReadGameState(string value, GameState fallback)
    {
        return Enum.TryParse<GameState>(value, ignoreCase: true, out var result) ? result : fallback;
    }

    private static MapType ReadMap(string value, MapType fallback)
    {
        return Enum.TryParse<MapType>(value, ignoreCase: true, out var result) ? result : fallback;
    }

    private static bool TryReadLobbySettings(JsonElement element, out LobbySettings settings)
    {
        settings = new LobbySettings();
        if (!TryGetDouble(element, "maxDistance", "MaxDistance", out var maxDistance))
        {
            return false;
        }

        settings.MaxDistance = maxDistance;
        settings.VisionHearing = GetBool(element, "visionHearing", "VisionHearing");
        settings.Haunting = GetBool(element, "haunting", "Haunting");
        settings.ThirdPartyHaunting = GetBool(element, "thirdPartyHaunting", "ThirdPartyHaunting");
        settings.HearImpostorsInVents = GetBool(element, "hearImpostorsInVents", "HearImpostorsInVents");
        settings.ImpostorsHearImpostorsInVent = GetBool(element, "impostersHearImpostersInvent", "ImpostorsHearImpostorsInVent");
        settings.ImpostorRadioEnabled = GetBool(element, "impostorRadioEnabled", "ImpostorRadioEnabled");
        settings.CommsSabotage = GetBool(element, "commsSabotage", "CommsSabotage");
        settings.VoiceEffectEnabled = GetBool(element, "voiceEffectEnabled", "VoiceEffectEnabled", true);
        settings.DeadOnly = GetBool(element, "deadOnly", "DeadOnly");
        settings.MeetingGhostOnly = GetBool(element, "meetingGhostOnly", "MeetingGhostOnly");
        settings.HearThroughCameras = GetBool(element, "hearThroughCameras", "HearThroughCameras");
        settings.WallsBlockAudio = GetBool(element, "wallsBlockAudio", "WallsBlockAudio");
        settings.PublicLobbyOn = GetBool(element, "publicLobby_on", "PublicLobbyOn");
        settings.PublicLobbyTitle = GetString(element, "publicLobby_title", "PublicLobbyTitle");
        settings.PublicLobbyLanguage = GetString(element, "publicLobby_language", "PublicLobbyLanguage", "ja");
        settings.PublicLobbyMods = GetString(element, "publicLobby_mods", "PublicLobbyMods", "NONE");
        return true;
    }

    private static LobbySettings CloneLobbySettings(LobbySettings settings)
    {
        return new LobbySettings
        {
            MaxDistance = settings.MaxDistance,
            VisionHearing = settings.VisionHearing,
            Haunting = settings.Haunting,
            ThirdPartyHaunting = settings.ThirdPartyHaunting,
            HearImpostorsInVents = settings.HearImpostorsInVents,
            ImpostorsHearImpostorsInVent = settings.ImpostorsHearImpostorsInVent,
            ImpostorRadioEnabled = settings.ImpostorRadioEnabled,
            CommsSabotage = settings.CommsSabotage,
            VoiceEffectEnabled = settings.VoiceEffectEnabled,
            DeadOnly = settings.DeadOnly,
            MeetingGhostOnly = settings.MeetingGhostOnly,
            HearThroughCameras = settings.HearThroughCameras,
            WallsBlockAudio = settings.WallsBlockAudio,
            PublicLobbyOn = settings.PublicLobbyOn,
            PublicLobbyTitle = settings.PublicLobbyTitle,
            PublicLobbyLanguage = settings.PublicLobbyLanguage,
            PublicLobbyMods = settings.PublicLobbyMods
        };
    }

    private static object ToOriginalLobbySettingsShape(LobbySettings settings)
    {
        return new
        {
            maxDistance = settings.MaxDistance,
            visionHearing = settings.VisionHearing,
            haunting = settings.Haunting,
            thirdPartyHaunting = settings.ThirdPartyHaunting,
            hearImpostorsInVents = settings.HearImpostorsInVents,
            impostersHearImpostersInvent = settings.ImpostorsHearImpostorsInVent,
            impostorRadioEnabled = settings.ImpostorRadioEnabled,
            commsSabotage = settings.CommsSabotage,
            voiceEffectEnabled = settings.VoiceEffectEnabled,
            deadOnly = settings.DeadOnly,
            meetingGhostOnly = settings.MeetingGhostOnly,
            hearThroughCameras = settings.HearThroughCameras,
            wallsBlockAudio = settings.WallsBlockAudio,
            publicLobby_on = settings.PublicLobbyOn,
            publicLobby_title = settings.PublicLobbyTitle,
            publicLobby_language = settings.PublicLobbyLanguage,
            publicLobby_mods = settings.PublicLobbyMods
        };
    }

    private static object ToOriginalSettingsShape(AppSettings settings)
    {
        var lobby = settings.LocalLobbySettings;
        return new
        {
            maxDistance = lobby.MaxDistance,
            visionHearing = lobby.VisionHearing,
            haunting = lobby.Haunting,
            thirdPartyHaunting = lobby.ThirdPartyHaunting,
            hearImpostorsInVents = lobby.HearImpostorsInVents,
            impostersHearImpostersInvent = lobby.ImpostorsHearImpostorsInVent,
            impostorRadioEnabled = lobby.ImpostorRadioEnabled,
            commsSabotage = lobby.CommsSabotage,
            voiceEffectEnabled = lobby.VoiceEffectEnabled,
            deadOnly = lobby.DeadOnly,
            meetingGhostOnly = lobby.MeetingGhostOnly,
            hearThroughCameras = lobby.HearThroughCameras,
            wallsBlockAudio = lobby.WallsBlockAudio,
            publicLobby_on = lobby.PublicLobbyOn,
            publicLobby_title = lobby.PublicLobbyTitle,
            publicLobby_language = lobby.PublicLobbyLanguage,
            publicLobby_mods = lobby.PublicLobbyMods,
            natFix = settings.NatFix
        };
    }

    private static bool TryGetDouble(JsonElement element, string camelName, string pascalName, out double value)
    {
        value = 0;
        if (!TryGetProperty(element, camelName, pascalName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.Number ? property.GetDouble() : 0;
        return property.ValueKind == JsonValueKind.Number;
    }

    private static bool GetBool(JsonElement element, string camelName, string pascalName, bool fallback = false)
    {
        return TryGetProperty(element, camelName, pascalName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
    }

    private static bool GetBool(JsonElement element, string name, bool fallback = false)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
    }

    private static int GetInt(JsonElement element, string name, int fallback = 0)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : fallback;
    }

    private static double GetDouble(JsonElement element, string name, double fallback = 0)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number ? property.GetDouble() : fallback;
    }

    private static string GetString(JsonElement element, string camelName, string pascalName, string fallback = "")
    {
        return TryGetProperty(element, camelName, pascalName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static string GetString(JsonElement element, string name, string fallback = "")
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static bool TryGetProperty(JsonElement element, string camelName, string pascalName, out JsonElement property)
    {
        return element.TryGetProperty(camelName, out property) || element.TryGetProperty(pascalName, out property);
    }

    private static object ToVoiceFramePayload(VoiceAudioFrame frame)
    {
        return new
        {
            buffer = Convert.ToBase64String(frame.Buffer),
            encoding = frame.Format.Encoding.ToString(),
            sampleRate = frame.Format.SampleRate,
            bitsPerSample = frame.Format.BitsPerSample,
            channels = frame.Format.Channels,
            level = frame.Level
        };
    }

    private static VoiceAudioFrame? ReadVoiceFrame(JsonElement element)
    {
        if (!element.TryGetProperty("buffer", out var bufferElement) ||
            bufferElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var bufferText = bufferElement.GetString();
        if (string.IsNullOrWhiteSpace(bufferText))
        {
            return null;
        }

        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(bufferText);
        }
        catch (FormatException)
        {
            return null;
        }

        var sampleRate = GetInt(element, "sampleRate", 48000);
        var bitsPerSample = GetInt(element, "bitsPerSample", 16);
        var channels = Math.Clamp(GetInt(element, "channels", 1), 1, 2);
        var encoding = GetString(element, "encoding", WaveFormatEncoding.Pcm.ToString());
        var format = string.Equals(encoding, WaveFormatEncoding.IeeeFloat.ToString(), StringComparison.OrdinalIgnoreCase)
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new WaveFormat(sampleRate, bitsPerSample, channels);
        var level = GetDouble(element, "level");
        return new VoiceAudioFrame(buffer, format, level, isTransmitting: true);
    }
}
