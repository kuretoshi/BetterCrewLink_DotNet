using System.Text.Json;
using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class PeerDataPayloadEventArgs : EventArgs
{
    public PeerDataPayloadEventArgs(string socketId, string payload)
    {
        SocketId = socketId;
        Payload = payload;
    }

    public string SocketId { get; }

    public string Payload { get; }
}

public sealed class PeerAudioFrameEventArgs : EventArgs
{
    public PeerAudioFrameEventArgs(string socketId, VoiceAudioFrame frame)
    {
        SocketId = socketId;
        Frame = frame;
    }

    public string SocketId { get; }

    public VoiceAudioFrame Frame { get; }
}

public sealed class PeerConnectionService
{
    private static readonly IReadOnlyList<string> DefaultIceServers = ["stun:stun.l.google.com:19302"];
    private static readonly IReadOnlyList<string> NatFixIceServers =
    [
        "stun:stun.l.google.com:19302",
        "stun:global.stun.twilio.com:3478",
        "stun:stun.cloudflare.com:3478"
    ];

    private readonly Dictionary<string, VoicePeerConnection> peers = [];
    private bool useNatFix;
    private IReadOnlyList<string> iceServers = DefaultIceServers;

    public IReadOnlyDictionary<string, VoicePeerConnection> Peers => peers;

    public bool UseNatFix => useNatFix;

    public IReadOnlyList<string> IceServers => iceServers;

    public event EventHandler? PeersChanged;

    public event EventHandler<PeerDataPayloadEventArgs>? DataPayloadQueued;

    public event EventHandler<PeerAudioFrameEventArgs>? AudioFrameQueued;

    public void ConfigureNatFix(bool enabled)
    {
        if (useNatFix == enabled)
        {
            return;
        }

        useNatFix = enabled;
        iceServers = enabled ? NatFixIceServers : DefaultIceServers;
        foreach (var peer in peers.Values)
        {
            peer.UseNatFix = useNatFix;
            peer.IceServers = iceServers;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
        }

        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    public VoicePeerConnection EnsurePeer(string socketId, VoiceClient client, bool initiator)
    {
        if (string.IsNullOrWhiteSpace(socketId))
        {
            throw new ArgumentException("Socket id is empty.", nameof(socketId));
        }

        if (!peers.TryGetValue(socketId, out var peer))
        {
            peer = new VoicePeerConnection
            {
                SocketId = socketId,
                PlayerId = client.PlayerId,
                ClientId = client.ClientId,
                Initiator = initiator,
                UseNatFix = useNatFix,
                IceServers = iceServers,
                State = VoicePeerConnectionState.Connecting
            };
            peers[socketId] = peer;
        }
        else if (peer.State is VoicePeerConnectionState.Disconnected or VoicePeerConnectionState.Failed)
        {
            peer.State = VoicePeerConnectionState.Connecting;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
        }

        PeersChanged?.Invoke(this, EventArgs.Empty);
        return peer;
    }

    public void ApplySignal(string socketId, VoiceClient client, JsonElement signal)
    {
        var isOffer = signal.TryGetProperty("type", out var type) &&
                      string.Equals(type.GetString(), "offer", StringComparison.OrdinalIgnoreCase);
        var peer = EnsurePeer(socketId, client, initiator: false);
        if (isOffer || peer.LastSignal is null)
        {
            peer.LastSignal = signal.Clone();
        }

        peer.State = VoicePeerConnectionState.Connecting;
        peer.UpdatedAt = DateTimeOffset.UtcNow;
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkConnected(string socketId)
    {
        if (peers.TryGetValue(socketId, out var peer))
        {
            peer.State = VoicePeerConnectionState.Connected;
            peer.ReconnectAttempts = 0;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
            PeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void MarkFailed(string socketId)
    {
        if (peers.TryGetValue(socketId, out var peer))
        {
            peer.State = VoicePeerConnectionState.Failed;
            peer.ReconnectAttempts++;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
            PeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemovePeer(string socketId)
    {
        if (peers.Remove(socketId))
        {
            PeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        if (peers.Count == 0)
        {
            return;
        }

        peers.Clear();
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void QueueDataPayload(string socketId, string payload)
    {
        if (peers.ContainsKey(socketId))
        {
            DataPayloadQueued?.Invoke(this, new PeerDataPayloadEventArgs(socketId, payload));
        }
    }

    public void BroadcastDataPayload(string payload)
    {
        foreach (var socketId in peers.Keys.ToList())
        {
            QueueDataPayload(socketId, payload);
        }
    }

    public void QueueAudioFrame(string socketId, VoiceAudioFrame frame)
    {
        if (peers.TryGetValue(socketId, out var peer) &&
            peer.State is VoicePeerConnectionState.Connecting or VoicePeerConnectionState.Connected)
        {
            AudioFrameQueued?.Invoke(this, new PeerAudioFrameEventArgs(socketId, frame));
        }
    }

    public void BroadcastAudioFrame(VoiceAudioFrame frame)
    {
        foreach (var socketId in peers.Keys.ToList())
        {
            QueueAudioFrame(socketId, frame);
        }
    }
}
