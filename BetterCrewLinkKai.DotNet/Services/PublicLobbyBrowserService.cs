using System.Text.Json;
using BetterCrewLinkKai.DotNet.Models;
using SocketIOClient;
using SocketIOClient.Transport;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class PublicLobbyBrowserService : IDisposable
{
    private readonly Dictionary<int, PublicLobby> lobbies = [];
    private SocketIOClient.SocketIO? socket;
    private bool disposed;

    public IReadOnlyCollection<PublicLobby> Lobbies => lobbies.Values;

    public bool IsConnected => socket?.Connected == true;

    public event EventHandler? LobbiesChanged;

    public event EventHandler<string>? Error;

    public async Task StartAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopAsync();

        socket = new SocketIOClient.SocketIO(NormalizeServerUrl(serverUrl), new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            Reconnection = true
        });

        RegisterHandlers(socket);
        await socket.ConnectAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            await socket.EmitAsync("lobbybrowser", false);
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
            lobbies.Clear();
            LobbiesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RequestLobbyCodeAsync(int lobbyId, Action<int, string, string, PublicLobby?> callback)
    {
        if (socket is null || !IsConnected)
        {
            callback(1, "公開ロビーサーバーに接続していません。", string.Empty, null);
            return;
        }

        await socket.EmitAsync("join_lobby", response =>
        {
            var state = response.GetValue<int>(0);
            var codeOrError = response.GetValue<string>(1);
            var server = response.GetValue<string>(2);
            PublicLobby? lobby = null;
            try
            {
                lobby = ReadLobby(response.GetValue<JsonElement>(3));
            }
            catch
            {
                lobby = null;
            }

            callback(state, codeOrError, server, lobby);
        }, lobbyId);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }

    private void RegisterHandlers(SocketIOClient.SocketIO nextSocket)
    {
        nextSocket.OnConnected += async (_, _) =>
        {
            await nextSocket.EmitAsync("lobbybrowser", true);
        };

        nextSocket.OnError += (_, error) =>
        {
            Error?.Invoke(this, $"公開ロビーの取得に失敗しました: {error}");
        };

        nextSocket.On("update_lobby", response =>
        {
            var lobby = ReadLobby(response.GetValue<JsonElement>());
            lobbies[lobby.Id] = lobby;
            LobbiesChanged?.Invoke(this, EventArgs.Empty);
        });

        nextSocket.On("new_lobbies", response =>
        {
            foreach (var lobbyElement in response.GetValue<JsonElement>().EnumerateArray())
            {
                var lobby = ReadLobby(lobbyElement);
                lobbies[lobby.Id] = lobby;
            }

            LobbiesChanged?.Invoke(this, EventArgs.Empty);
        });

        nextSocket.On("remove_lobby", response =>
        {
            var lobbyId = response.GetValue<int>();
            if (lobbies.Remove(lobbyId))
            {
                LobbiesChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private static PublicLobby ReadLobby(JsonElement element)
    {
        return new PublicLobby
        {
            Id = ReadInt(element, "id"),
            Title = ReadString(element, "title"),
            Host = ReadString(element, "host"),
            CurrentPlayers = ReadInt(element, "current_players"),
            MaxPlayers = ReadInt(element, "max_players"),
            Language = ReadString(element, "language", "ja"),
            Mods = ReadString(element, "mods", "NONE"),
            IsPublic = ReadBool(element, "isPublic"),
            Server = ReadString(element, "server"),
            GameState = ReadGameState(ReadInt(element, "gameState", -1)),
            StateTime = ReadLong(element, "stateTime")
        };
    }

    private static GameState ReadGameState(int value)
    {
        return value switch
        {
            0 => GameState.Lobby,
            1 => GameState.Tasks,
            2 => GameState.Discussion,
            3 => GameState.Menu,
            _ => GameState.Unknown
        };
    }

    private static int ReadInt(JsonElement element, string name, int fallback = 0)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : fallback;
    }

    private static long ReadLong(JsonElement element, string name, long fallback = 0)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : fallback;
    }

    private static bool ReadBool(JsonElement element, string name, bool fallback = false)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
    }

    private static string ReadString(JsonElement element, string name, string fallback = "")
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static string NormalizeServerUrl(string serverUrl)
    {
        var trimmed = serverUrl.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://{trimmed}";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
