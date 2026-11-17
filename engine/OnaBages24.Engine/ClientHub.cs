using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using OnaBages24.Engine.Protocol;

namespace OnaBages24.Engine;

/// <summary>
/// Gestiona els clients WebSocket connectats (la interfície, la consola de proves...):
/// rep ordres, envia respostes i difon esdeveniments a tothom.
/// </summary>
public sealed class ClientHub
{
    /// <summary>Mida màxima d'un missatge (D-140: 1 MB, perquè hi càpiga el logotip de l'emissora; abans, 64 KB).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly Action<int> _clientsChanged;
    private readonly ILogger _log;

    /// <param name="clientsChanged">Es crida amb el nombre de clients cada vegada que canvia.</param>
    public ClientHub(Action<int> clientsChanged, ILogger log)
    {
        _clientsChanged = clientsChanged;
        _log = log;
    }

    /// <summary>Atén un client fins que es desconnecta.</summary>
    public async Task RunClientAsync(WebSocket socket, CommandRouter router, CancellationToken ct)
    {
        var client = new Client(socket);
        _clients[client.Id] = client;
        _clientsChanged(_clients.Count);
        _log.LogInformation("Client connectat ({Count} en total)", _clients.Count);

        var buffer = new byte[4096];
        using var message = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (received.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer, 0, received.Count);

                if (message.Length > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Missatge massa gran", ct);
                    break;
                }

                if (!received.EndOfMessage)
                    continue;

                if (received.MessageType == WebSocketMessageType.Text)
                {
                    string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    if (CommandRouter.IsSlow(text))
                    {
                        // Una ordre que pot tardar (el correu de prova, D-160): a part, perquè les altres ordres d'aquest
                        // client no l'hagin d'esperar. La resposta porta l'id: pot arribar després d'altres respostes.
                        _ = Task.Run(async () =>
                        {
                            try { await client.SendAsync(router.Handle(text, client.Context), ct); }
                            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                        });
                    }
                    else
                    {
                        JsonObject response = router.Handle(text, client.Context);
                        await client.SendAsync(response, ct);
                    }
                }

                message.SetLength(0);
            }
        }
        catch (OperationCanceledException)
        {
            // El motor s'atura o el client ha marxat.
        }
        catch (WebSocketException ex)
        {
            _log.LogInformation("Connexió tancada de manera abrupta: {Message}", ex.Message);
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            _clientsChanged(_clients.Count);
            await client.TryCloseAsync();
            _log.LogInformation("Client desconnectat ({Count} en total)", _clients.Count);
        }
    }

    /// <summary>
    /// Les sessions que s'han tancat per inactivitat (D-164): a les connexions que les tenien, se'ls treu i se'ls envia
    /// <c>session.changed</c> amb l'estat nou (mode de consulta).
    /// </summary>
    public async Task EndSessionsAsync(IReadOnlyCollection<string> sessionIds, CommandRouter router, CancellationToken ct)
    {
        foreach (var client in _clients.Values)
        {
            if (client.Context.SessionId is not { } id || !sessionIds.Contains(id)) continue;
            client.Context.SessionId = null;
            var ev = Messages.Event("session.changed");
            foreach (var (key, value) in router.SessionJson(client.Context)) ev[key] = value?.DeepClone();
            ev["reason"] = "inactivity";
            try { await client.SendAsync(ev, ct); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>Envia un esdeveniment a tots els clients connectats.</summary>
    public async Task BroadcastAsync(JsonObject ev, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ev.ToJsonString());
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.SendAsync(bytes, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // Aquest client ja no hi és; se'n traurà quan acabi el seu bucle.
            }
        }
    }

    private sealed class Client
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public Client(WebSocket socket) => Socket = socket;

        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>La sessió d'aquesta connexió (D-164).</summary>
        public ClientContext Context { get; } = new();

        public WebSocket Socket { get; }

        public Task SendAsync(JsonObject message, CancellationToken ct) =>
            SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), ct);

        public async Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            // Un WebSocket no admet dos enviaments alhora: els posem en fila.
            await _sendLock.WaitAsync(ct);
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task TryCloseAsync()
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Adéu", CancellationToken.None);
            }
            catch (Exception)
            {
                // Ja tancat.
            }
        }
    }
}
