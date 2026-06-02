using System.Net;
using System.Text;
using System.Text.Json;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class ObsOverlayService : IDisposable
{
    public const string Url = "http://127.0.0.1:47777/";

    private readonly object syncRoot = new();
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private HttpListener? listener;
    private CancellationTokenSource? cancellationTokenSource;
    private Task? listenTask;
    private ObsOverlaySnapshot snapshot = ObsOverlaySnapshot.Empty;
    private string requiredSecret = string.Empty;

    public bool IsRunning => listener?.IsListening == true;

    public void Start(string secret)
    {
        if (IsRunning)
        {
            requiredSecret = secret;
            return;
        }

        requiredSecret = secret;
        cancellationTokenSource = new CancellationTokenSource();
        listener = new HttpListener();
        listener.Prefixes.Add(Url);
        listener.Start();
        listenTask = Task.Run(() => ListenAsync(cancellationTokenSource.Token));
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
        if (listener is not null)
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        listener = null;
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
        listenTask = null;
    }

    public void UpdateSnapshot(ObsOverlaySnapshot nextSnapshot)
    {
        lock (syncRoot)
        {
            snapshot = nextSnapshot;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener is { IsListening: true } currentListener)
        {
            HttpListenerContext context;
            try
            {
                context = await currentListener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        if (!IsAuthorized(context.Request))
        {
            await WriteUnauthorizedAsync(context);
            return;
        }

        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (string.Equals(path, "/state", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context);
            return;
        }

        await WriteHtmlAsync(context);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        return string.IsNullOrWhiteSpace(requiredSecret) ||
               string.Equals(request.QueryString["secret"], requiredSecret, StringComparison.Ordinal);
    }

    private static async Task WriteUnauthorizedAsync(HttpListenerContext context)
    {
        var bytes = Encoding.UTF8.GetBytes("Unauthorized");
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task WriteJsonAsync(HttpListenerContext context)
    {
        ObsOverlaySnapshot currentSnapshot;
        lock (syncRoot)
        {
            currentSnapshot = snapshot;
        }

        var json = JsonSerializer.Serialize(currentSnapshot, jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context)
    {
        var bytes = Encoding.UTF8.GetBytes(Html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private const string Html = """
<!doctype html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>BetterCrewLinkKai OBS Overlay</title>
  <style>
    html, body { margin: 0; background: transparent; overflow: hidden; font-family: "Segoe UI", sans-serif; }
    #overlay { min-width: 220px; max-width: 420px; color: white; padding: 10px; }
    .panel { background: rgba(29, 26, 35, .78); border: 1px solid rgba(206, 147, 216, .45); border-radius: 8px; padding: 10px; }
    .local { display: grid; grid-template-columns: 42px 1fr auto; gap: 8px; align-items: center; margin-bottom: 8px; }
    .avatar { width: 34px; height: 34px; border-radius: 50%; border: 2px solid #2ecc71; background: var(--color, #50ef39); box-shadow: inset -9px -6px 0 var(--shadow, #15a742); }
    .name { font-size: 16px; font-weight: 700; text-shadow: 0 2px 4px #000; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .code { display: inline-block; background: rgba(75,75,75,.9); border-radius: 4px; padding: 1px 7px; font: 700 18px Consolas, monospace; letter-spacing: .5px; }
    .flags { display: flex; flex-direction: column; gap: 4px; align-items: center; font-size: 14px; }
    .players { display: flex; flex-wrap: wrap; gap: 8px; }
    .player { width: 52px; text-align: center; opacity: var(--opacity, 1); }
    .player .avatar { width: 30px; height: 30px; margin: 0 auto 2px; border-color: var(--ring, #2ecc71); border-width: var(--ring-width, 2px); }
    .player .label { font-size: 10px; text-shadow: 0 2px 4px #000; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .badge { display: inline-block; min-width: 14px; padding: 1px 3px; border-radius: 8px; background: #ff174f; font-size: 9px; margin-left: -10px; transform: translateY(-7px); }
  </style>
</head>
<body>
  <div id="overlay"></div>
  <script>
    const secret = new URLSearchParams(location.search).get('secret') || '';
    async function refresh() {
      const response = await fetch('/state?secret=' + encodeURIComponent(secret), { cache: 'no-store' });
      if (!response.ok) {
        document.getElementById('overlay').innerHTML = '';
        return;
      }
      const state = await response.json();
      const root = document.getElementById('overlay');
      if (!state.visible) {
        root.innerHTML = '';
        return;
      }

      const local = state.localPlayer;
      const players = state.players || [];
      root.innerHTML = `
        <div class="panel">
          <div class="local">
            <div class="avatar" style="--color:${local.mainColor};--shadow:${local.shadowColor}"></div>
            <div>
              <div class="name">${escapeHtml(local.name)}</div>
              <div class="code">${escapeHtml(local.lobbyCode)}</div>
            </div>
            <div class="flags">
              <span>${local.muted ? 'mic off' : 'mic'}</span>
              <span>${local.deafened ? 'sound off' : 'sound'}</span>
            </div>
          </div>
          <div class="players">${players.map(renderPlayer).join('')}</div>
        </div>`;
    }

    function renderPlayer(player) {
      const ring = player.talking ? '#ce93d8' : '#2ecc71';
      const opacity = player.audible ? 1 : .45;
      const badges = `${player.usingRadio ? '<span class="badge">R</span>' : ''}${player.muted ? '<span class="badge">M</span>' : ''}`;
      return `<div class="player" style="--opacity:${opacity}">
        <div class="avatar" style="--color:${player.mainColor};--shadow:${player.shadowColor};--ring:${ring};--ring-width:${player.talking ? 3 : 2}px"></div>
        ${badges}
        <div class="label">${escapeHtml(player.name)}</div>
      </div>`;
    }

    function escapeHtml(value) {
      return String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
    }

    refresh();
    setInterval(refresh, 250);
  </script>
</body>
</html>
""";
}

public sealed record ObsOverlaySnapshot(
    bool Visible,
    ObsOverlayLocalPlayer LocalPlayer,
    IReadOnlyList<ObsOverlayPlayer> Players)
{
    public static ObsOverlaySnapshot Empty { get; } = new(
        false,
        new ObsOverlayLocalPlayer("ROBBER", "MENU", "#50EF39", "#15A742", false, false),
        []);
}

public sealed record ObsOverlayLocalPlayer(
    string Name,
    string LobbyCode,
    string MainColor,
    string ShadowColor,
    bool Muted,
    bool Deafened);

public sealed record ObsOverlayPlayer(
    string Name,
    string MainColor,
    string ShadowColor,
    bool Talking,
    bool UsingRadio,
    bool Audible,
    bool Muted);
