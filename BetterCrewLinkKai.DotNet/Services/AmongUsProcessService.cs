using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class AmongUsProcessService : IDisposable
{
    private CancellationTokenSource? cancellationTokenSource;
    private Task? monitorTask;
    private int? currentProcessId;

    public event EventHandler<AmongUsProcessInfo?>? ProcessChanged;

    public AmongUsProcessInfo? Current { get; private set; }

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
        cancellationTokenSource?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));

        while (!cancellationToken.IsCancellationRequested)
        {
            var next = TryFindAmongUs();
            var nextProcessId = next?.ProcessId;

            if (nextProcessId != currentProcessId)
            {
                currentProcessId = nextProcessId;
                Current = next;
                ProcessChanged?.Invoke(this, next);
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

    private static AmongUsProcessInfo? TryFindAmongUs()
    {
        foreach (var process in Process.GetProcessesByName("Among Us"))
        {
            try
            {
                var processPath = process.MainModule?.FileName ?? string.Empty;
                var gameDirectory = Path.GetDirectoryName(processPath) ?? string.Empty;
                var hasGameAssembly = process.Modules
                    .Cast<ProcessModule>()
                    .Any(static module => string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));

                return new AmongUsProcessInfo
                {
                    ProcessId = process.Id,
                    ProcessPath = processPath,
                    GameDirectory = gameDirectory,
                    HasGameAssembly = hasGameAssembly,
                    Is64Bit = IsProcess64Bit(process),
                    InstalledMod = DetectInstalledMod(gameDirectory)
                };
            }
            catch
            {
                // Access can fail when the app is not elevated but the game is.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static AmongUsMod DetectInstalledMod(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return AmongUsMod.KnownMods[0];
        }

        var pluginsDirectory = Path.Combine(gameDirectory, "BepInEx", "plugins");
        if (!File.Exists(Path.Combine(gameDirectory, "winhttp.dll")) || !Directory.Exists(pluginsDirectory))
        {
            return AmongUsMod.KnownMods[0];
        }

        foreach (var file in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            var mod = AmongUsMod.KnownMods.FirstOrDefault(candidate =>
                candidate.DllStartsWith is not null &&
                fileName.StartsWith(candidate.DllStartsWith, StringComparison.OrdinalIgnoreCase));

            if (mod is not null)
            {
                return mod;
            }
        }

        return AmongUsMod.KnownMods.First(static mod => mod.Id == AmongUsModType.Other);
    }

    private static bool IsProcess64Bit(Process process)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        return IsWow64Process(process.Handle, out var isWow64) && !isWow64;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr processHandle, out bool wow64Process);
}
