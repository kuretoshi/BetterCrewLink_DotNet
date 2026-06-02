namespace BetterCrewLinkKai.DotNet.Models;

public sealed class PublicLobby
{
    public int Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public int CurrentPlayers { get; init; }

    public int MaxPlayers { get; init; }

    public string Language { get; init; } = "ja";

    public string Mods { get; init; } = "NONE";

    public bool IsPublic { get; init; }

    public string Server { get; init; } = string.Empty;

    public GameState GameState { get; init; } = GameState.Unknown;

    public long StateTime { get; init; }
}
