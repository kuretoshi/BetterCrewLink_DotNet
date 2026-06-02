using BetterCrewLinkKai.DotNet.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class VoicePlaybackService : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly Dictionary<string, RemoteVoicePlayer> players = [];
    private string speakerId = "Default";
    private bool isDeafened;
    private bool disposed;

    public void SetDeafened(bool deafened)
    {
        isDeafened = deafened;
        foreach (var player in players.Values)
        {
            player.SetDeafened(isDeafened);
        }
    }

    public void SetSpeaker(string deviceId)
    {
        speakerId = string.IsNullOrWhiteSpace(deviceId) ? "Default" : deviceId;
        foreach (var player in players.Values)
        {
            player.Dispose();
        }

        players.Clear();
    }

    public void SubmitFrame(string socketId, VoiceAudioFrame frame)
    {
        if (disposed || string.IsNullOrWhiteSpace(socketId))
        {
            return;
        }

        if (!players.TryGetValue(socketId, out var player) ||
            !WaveFormatMatches(player.WaveFormat, frame.Format))
        {
            player?.Dispose();
            player = CreatePlayer(frame.Format);
            players[socketId] = player;
        }

        player.Submit(frame.Buffer);
    }

    public void ApplyMix(IEnumerable<VoicePeerState> peers)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var peer in peers)
        {
            active.Add(peer.SocketId);
            if (players.TryGetValue(peer.SocketId, out var player))
            {
                player.ApplyMix(peer.Mix);
            }
        }

        foreach (var staleSocketId in players.Keys.Where(socketId => !active.Contains(socketId)).ToList())
        {
            players[staleSocketId].Dispose();
            players.Remove(staleSocketId);
        }
    }

    public void RemovePeer(string socketId)
    {
        if (players.Remove(socketId, out var player))
        {
            player.Dispose();
        }
    }

    public void Clear()
    {
        foreach (var player in players.Values)
        {
            player.Dispose();
        }

        players.Clear();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Clear();
        enumerator.Dispose();
    }

    private RemoteVoicePlayer CreatePlayer(WaveFormat waveFormat)
    {
        var device = GetRenderDevice(speakerId);
        var output = device is null
            ? new WasapiOut(AudioClientShareMode.Shared, 80)
            : new WasapiOut(device, AudioClientShareMode.Shared, false, 80);

        var buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(350),
            DiscardOnBufferOverflow = true
        };
        var mixProvider = new VoiceMixWaveProvider(buffer);
        mixProvider.IsDeafened = isDeafened;
        output.Init(mixProvider);
        output.Play();
        return new RemoteVoicePlayer(output, buffer, mixProvider);
    }

    private MMDevice? GetRenderDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.Equals(deviceId, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(device => string.Equals(device.ID, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool WaveFormatMatches(WaveFormat left, WaveFormat right)
    {
        return left.Encoding == right.Encoding &&
               left.SampleRate == right.SampleRate &&
               left.BitsPerSample == right.BitsPerSample &&
               left.Channels == right.Channels;
    }

    private sealed class RemoteVoicePlayer : IDisposable
    {
        private readonly IWavePlayer output;
        private readonly BufferedWaveProvider buffer;
        private readonly VoiceMixWaveProvider mixProvider;

        public RemoteVoicePlayer(IWavePlayer output, BufferedWaveProvider buffer, VoiceMixWaveProvider mixProvider)
        {
            this.output = output;
            this.buffer = buffer;
            this.mixProvider = mixProvider;
        }

        public WaveFormat WaveFormat => buffer.WaveFormat;

        public void Submit(byte[] bytes)
        {
            buffer.AddSamples(bytes, 0, bytes.Length);
        }

        public void SetDeafened(bool deafened)
        {
            mixProvider.IsDeafened = deafened;
        }

        public void ApplyMix(PlayerVoiceMix? mix)
        {
            mixProvider.Volume = mix?.Volume ?? 0;
            mixProvider.Pan = mix is null ? 0 : Math.Clamp(mix.PanX / 6d, -1d, 1d);
            mixProvider.IsMuffled = mix?.IsMuffled == true;
            mixProvider.UsesGhostReverb = mix?.UsesGhostReverb == true;
            mixProvider.UsesVoiceEffect = mix?.UsesVoiceEffect == true;
            mixProvider.VoiceEffectStrength = Math.Clamp((mix?.VoiceEffectStrength ?? 0) / 100d, 0d, 2d);
        }

        public void Dispose()
        {
            output.Stop();
            output.Dispose();
        }
    }

    private sealed class VoiceMixWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider source;
        private readonly double[] reverbLeft;
        private readonly double[] reverbRight;
        private double lowPassLeft;
        private double lowPassRight;
        private double bandPassLeft;
        private double bandPassRight;
        private double bandRejectLeft;
        private double bandRejectRight;
        private int reverbIndex;

        public VoiceMixWaveProvider(IWaveProvider source)
        {
            this.source = source;
            WaveFormat = source.WaveFormat;
            var delaySamples = Math.Max(1, WaveFormat.SampleRate / 9);
            reverbLeft = new double[delaySamples];
            reverbRight = new double[delaySamples];
        }

        public WaveFormat WaveFormat { get; }

        public double Volume { get; set; } = 1;

        public double Pan { get; set; }

        public bool IsMuffled { get; set; }

        public bool IsDeafened { get; set; }

        public bool UsesGhostReverb { get; set; }

        public bool UsesVoiceEffect { get; set; }

        public double VoiceEffectStrength { get; set; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read <= 0 || Volume <= 0 || IsDeafened)
            {
                if (read > 0)
                {
                    Array.Clear(buffer, offset, read);
                }

                return read;
            }

            var bytesPerSample = WaveFormat.BitsPerSample / 8;
            if (WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
            {
                ProcessFloat(buffer, offset, read);
            }
            else if (bytesPerSample == 2)
            {
                ProcessPcm16(buffer, offset, read);
            }

            return read;
        }

        private void ProcessFloat(byte[] buffer, int offset, int count)
        {
            var channels = Math.Max(1, WaveFormat.Channels);
            var sampleIndex = 0;
            for (var i = offset; i + 3 < offset + count; i += 4)
            {
                var channel = sampleIndex++ % channels;
                var sample = BitConverter.ToSingle(buffer, i);
                sample = (float)ProcessSample(sample, channel);
                BitConverter.GetBytes(sample).CopyTo(buffer, i);
            }
        }

        private void ProcessPcm16(byte[] buffer, int offset, int count)
        {
            var channels = Math.Max(1, WaveFormat.Channels);
            var sampleIndex = 0;
            for (var i = offset; i + 1 < offset + count; i += 2)
            {
                var channel = sampleIndex++ % channels;
                var sample = BitConverter.ToInt16(buffer, i) / 32768d;
                var processed = (short)Math.Clamp(ProcessSample(sample, channel) * 32767, short.MinValue, short.MaxValue);
                BitConverter.GetBytes(processed).CopyTo(buffer, i);
            }
        }

        private double ProcessSample(double sample, int channel)
        {
            var panGain = 1d;
            if (WaveFormat.Channels >= 2)
            {
                panGain = channel == 0
                    ? Math.Clamp(1d - Math.Max(0d, Pan), 0d, 1d)
                    : Math.Clamp(1d + Math.Min(0d, Pan), 0d, 1d);
            }

            if (IsMuffled)
            {
                if (channel == 0)
                {
                    lowPassLeft += 0.12d * (sample - lowPassLeft);
                    sample = lowPassLeft;
                }
                else
                {
                    lowPassRight += 0.12d * (sample - lowPassRight);
                    sample = lowPassRight;
                }
            }

            if (UsesVoiceEffect && VoiceEffectStrength > 0)
            {
                sample = ApplyVoiceEffect(sample, channel);
            }

            if (UsesGhostReverb)
            {
                sample = ApplyReverb(sample, channel);
            }

            return Math.Clamp(sample * Volume * panGain, -1d, 1d);
        }

        private double ApplyVoiceEffect(double sample, int channel)
        {
            var normalized = Math.Clamp(VoiceEffectStrength, 0d, 2d);
            var filterRate = 0.06d + Math.Min(0.18d, normalized * 0.05d);
            if (channel == 0)
            {
                bandPassLeft += filterRate * (sample - bandPassLeft);
                bandRejectLeft += 0.02d * (bandPassLeft - bandRejectLeft);
                return BlendVoiceEffect(sample, bandPassLeft - bandRejectLeft, normalized);
            }

            bandPassRight += filterRate * (sample - bandPassRight);
            bandRejectRight += 0.02d * (bandPassRight - bandRejectRight);
            return BlendVoiceEffect(sample, bandPassRight - bandRejectRight, normalized);
        }

        private static double BlendVoiceEffect(double dry, double wetSource, double normalized)
        {
            var wet = Math.Tanh(wetSource * (2.2d + normalized * 2.5d));
            var dryGain = 1d - Math.Min(0.9d, normalized * 0.65d);
            var wetGain = Math.Min(1.45d, normalized * 1.2d);
            return (dry * dryGain) + (wet * wetGain);
        }

        private double ApplyReverb(double sample, int channel)
        {
            var buffer = channel == 0 ? reverbLeft : reverbRight;
            var delayed = buffer[reverbIndex];
            buffer[reverbIndex] = sample + delayed * 0.45d;
            if (channel == WaveFormat.Channels - 1 || WaveFormat.Channels == 1)
            {
                reverbIndex = (reverbIndex + 1) % buffer.Length;
            }

            return sample * 0.82d + delayed * 0.28d;
        }
    }
}
