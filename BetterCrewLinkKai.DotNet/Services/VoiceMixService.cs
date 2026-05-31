using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class VoiceMixService
{
    public IReadOnlyDictionary<int, PlayerVoiceMix> Calculate(AmongUsState state, AppSettings settings)
    {
        var result = new Dictionary<int, PlayerVoiceMix>();
        var me = state.Players.FirstOrDefault(static player => player.IsLocal);
        if (me is null)
        {
            return result;
        }

        foreach (var other in state.Players.Where(static player => !player.IsLocal && !player.Disconnected))
        {
            result[other.ClientId] = CalculatePlayer(state, settings, me, other);
        }

        return result;
    }

    private static PlayerVoiceMix CalculatePlayer(AmongUsState state, AppSettings settings, Player me, Player other)
    {
        var lobby = settings.LocalLobbySettings;
        var volume = state.GameState switch
        {
            GameState.Lobby => 1d,
            GameState.Tasks => 1d,
            GameState.Discussion => 1d,
            _ => 0d
        };
        var reason = volume <= 0 ? "menu" : "audible";
        var panX = other.X - me.X;
        var panY = other.Y - me.Y;
        var isMuffled = false;
        var usesGhostReverb = false;
        var usesVoiceEffect = false;
        var skipDistanceCheck = false;

        if (volume <= 0)
        {
            return Muted(other, reason);
        }

        if (state.GameState == GameState.Tasks)
        {
            if (lobby.MeetingGhostOnly)
            {
                return Muted(other, "meeting-ghost-only");
            }

            if (!me.IsDead && lobby.CommsSabotage && state.CommsSabotaged && !me.IsImpostor)
            {
                return Muted(other, "comms-sabotage");
            }

            if (other.InVent && !(lobby.HearImpostorsInVents || (lobby.ImpostorsHearImpostorsInVent && me.InVent)))
            {
                return Muted(other, "other-in-vent");
            }

            if (me.IsImpostor && other.IsImpostor && lobby.ImpostorRadioEnabled)
            {
                skipDistanceCheck = true;
                isMuffled = true;
                reason = "impostor-radio";
            }

            if (!me.IsDead && other.IsDead)
            {
                if (CanHearGhosts(me, lobby))
                {
                    volume = settings.GhostVolumeAsImpostor / 100d;
                    usesGhostReverb = true;
                    reason = "ghost-audible";
                }
                else
                {
                    return Muted(other, "dead-player");
                }
            }

            if (((me.InVent && !me.IsDead) || (other.InVent && !other.IsDead)) && !skipDistanceCheck)
            {
                isMuffled = true;
                volume = Math.Min(volume, 0.5d);
                reason = "vent-muffled";
            }

            usesVoiceEffect = lobby.VoiceEffectEnabled &&
                              settings.VoiceEffectStrength > 0 &&
                              !me.IsDead &&
                              !other.IsDead &&
                              !isMuffled &&
                              IsAppearanceDisguised(other);
        }
        else if (state.GameState == GameState.Discussion)
        {
            panX = 0;
            panY = 0;
            if (!me.IsDead && other.IsDead)
            {
                return Muted(other, "dead-player-discussion");
            }
        }

        if (lobby.DeadOnly)
        {
            panX = 0;
            panY = 0;
            if (!me.IsDead || !other.IsDead)
            {
                return Muted(other, "dead-only");
            }
        }

        var distance = Math.Sqrt((panX * panX) + (panY * panY));
        if (!skipDistanceCheck && distance > lobby.MaxDistance)
        {
            return Muted(other, "out-of-range");
        }

        if (!settings.EnableSpatialAudio || skipDistanceCheck)
        {
            panX = 0;
            panY = 0;
        }

        var playerConfig = GetPlayerConfig(settings, other);
        if (playerConfig.IsMuted)
        {
            return Muted(other, "player-muted");
        }

        volume *= playerConfig.Volume;
        volume *= settings.MasterVolume / 100d;
        if (me.IsDead && !other.IsDead)
        {
            volume *= settings.CrewVolumeAsGhost / 100d;
        }

        return new PlayerVoiceMix
        {
            PlayerId = other.Id,
            ClientId = other.ClientId,
            Name = other.Name,
            Volume = Math.Clamp(volume, 0d, 2d),
            PanX = panX,
            PanY = panY,
            IsMuffled = isMuffled,
            UsesGhostReverb = usesGhostReverb,
            UsesVoiceEffect = usesVoiceEffect,
            Reason = reason
        };
    }

    private static bool CanHearGhosts(Player player, LobbySettings lobby)
    {
        return (player.IsImpostor && lobby.Haunting) ||
               (player.IsThirdParty && lobby.ThirdPartyHaunting);
    }

    private static bool IsAppearanceDisguised(Player player)
    {
        return !string.IsNullOrWhiteSpace(player.AppearanceId) &&
               !string.Equals(player.AppearanceId, $"{player.ColorId}|{player.HatId}|{player.SkinId}|{player.VisorId}", StringComparison.Ordinal);
    }

    private static SocketConfig GetPlayerConfig(AppSettings settings, Player player)
    {
        return settings.PlayerConfigMap.TryGetValue(player.NameHash.ToString(), out var byHash)
            ? byHash
            : settings.PlayerConfigMap.TryGetValue(player.Name, out var byName)
                ? byName
                : new SocketConfig();
    }

    private static PlayerVoiceMix Muted(Player player, string reason)
    {
        return new PlayerVoiceMix
        {
            PlayerId = player.Id,
            ClientId = player.ClientId,
            Name = player.Name,
            Volume = 0,
            Reason = reason
        };
    }
}
