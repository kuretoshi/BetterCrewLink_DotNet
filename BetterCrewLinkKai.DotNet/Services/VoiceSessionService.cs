using System.Text.Json;
using BetterCrewLinkKai.DotNet.Models;
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
    public int PlayerId { get; init; }

    public int ClientId { get; init; }

    public bool IsTalking { get; set; }
}

public sealed class VoiceSessionService : IDisposable
{
    private readonly VoiceMixService voiceMixService = new();
    private SocketIOClient.SocketIO? socket;
    private string currentLobby = "MENU";
    private string lastLobbyPresenceKey = string.Empty;
    private bool disposed;

    public VoiceSessionState State { get; private set; } = VoiceSessionState.Disconnected;

    public string StatusText { get; private set; } = "未接続";

    public int ServerHostId { get; private set; }

    public Dictionary<string, VoiceClient> SocketClients { get; } = [];

    public IReadOnlyDictionary<int, PlayerVoiceMix> VoiceMix { get; private set; } = new Dictionary<int, PlayerVoiceMix>();

    public event EventHandler? StateChanged;

    public event EventHandler<string>? Error;

    public async Task ConnectAsync(string serverUrl, string lobbyCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ArgumentException("サーバー URL が空です。", nameof(serverUrl));
        }

        State = VoiceSessionState.Connecting;
        StatusText = "接続中";
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
        StatusText = $"{serverUrl} に接続中";
        currentLobby = "MENU";
        lastLobbyPresenceKey = string.Empty;
        NotifyStateChanged();
    }

    public async Task UpdateGameStateAsync(AmongUsState state, AppSettings? settings = null, CancellationToken cancellationToken = default)
    {
        if (settings is not null)
        {
            VoiceMix = voiceMixService.Calculate(state, settings);
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
                StatusText = "ボイスロビーから退出";
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
            StatusText = $"ボイスロビー参加: {state.LobbyCode}";
            NotifyStateChanged();
        }

        await EmitLobbyPresenceAsync(state, settings, cancellationToken);
    }

    public async Task EmitVadAsync(bool talking, CancellationToken cancellationToken = default)
    {
        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        await socket.EmitAsync("VAD", talking, cancellationToken);
    }

    public void Disconnect()
    {
        DisconnectSocketAsync().GetAwaiter().GetResult();
        State = VoiceSessionState.Disconnected;
        StatusText = "未接続";
        currentLobby = "MENU";
        lastLobbyPresenceKey = string.Empty;
        SocketClients.Clear();
        VoiceMix = new Dictionary<int, PlayerVoiceMix>();
        NotifyStateChanged();
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
            StatusText = "ボイスサーバー接続済み";
            NotifyStateChanged();
        };

        nextSocket.OnDisconnected += (_, reason) =>
        {
            State = VoiceSessionState.Disconnected;
            StatusText = $"切断: {reason}";
            currentLobby = "MENU";
            lastLobbyPresenceKey = string.Empty;
            SocketClients.Clear();
            NotifyStateChanged();
        };

        nextSocket.OnError += (_, error) =>
        {
            StatusText = $"Socket.IO エラー: {error}";
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
            var client = ReadClient(response.GetValue<JsonElement>(1));
            if (!string.IsNullOrWhiteSpace(socketId))
            {
                SocketClients[socketId] = client;
                NotifyStateChanged();
            }
        });

        nextSocket.On("setClients", response =>
        {
            SocketClients.Clear();
            var clients = response.GetValue<JsonElement>();
            foreach (var clientProperty in clients.EnumerateObject())
            {
                SocketClients[clientProperty.Name] = ReadClient(clientProperty.Value);
            }

            NotifyStateChanged();
        });

        nextSocket.On("join", response =>
        {
            var socketId = response.GetValue<string>(0);
            var client = ReadClient(response.GetValue<JsonElement>(1));
            if (!string.IsNullOrWhiteSpace(socketId))
            {
                SocketClients[socketId] = client;
                NotifyStateChanged();
            }
        });

        nextSocket.On("leave", response =>
        {
            var socketId = response.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(socketId) && SocketClients.Remove(socketId))
            {
                NotifyStateChanged();
            }
        });

        nextSocket.On("VAD", response =>
        {
            var data = response.GetValue<JsonElement>();
            if (data.TryGetProperty("socketId", out var socketIdElement) &&
                SocketClients.TryGetValue(socketIdElement.GetString() ?? string.Empty, out var client))
            {
                client.IsTalking = data.TryGetProperty("activity", out var activity) && activity.GetBoolean();
                NotifyStateChanged();
            }
        });

        nextSocket.On("signal", _ =>
        {
            Error?.Invoke(this, "WebRTC signal received, but peer audio is not implemented yet.");
        });
    }

    private async Task EmitLobbyPresenceAsync(AmongUsState state, AppSettings? settings, CancellationToken cancellationToken)
    {
        if (socket is null || settings is null || string.Equals(state.LobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var lobby = settings.LocalLobbySettings;
        var key = string.Join('|',
            state.LobbyCode,
            state.IsHost,
            state.MaxPlayers,
            state.Map,
            state.CurrentServer,
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
            isPublic = lobby.PublicLobbyOn
        }, cancellationToken);

        if (state.IsHost)
        {
            await socket.EmitAsync("setHost", state.LobbyCode, state.ClientId, cancellationToken);
        }
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

    private static VoiceClient ReadClient(JsonElement element)
    {
        return new VoiceClient
        {
            PlayerId = element.TryGetProperty("playerId", out var playerId) ? playerId.GetInt32() : 0,
            ClientId = element.TryGetProperty("clientId", out var clientId) ? clientId.GetInt32() : 0
        };
    }
}
