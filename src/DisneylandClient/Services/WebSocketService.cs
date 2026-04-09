using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DisneylandClient.Services;

/// <summary>Represents the current WebSocket connection lifecycle state.</summary>
public enum WsConnectionState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>
/// Singleton service that maintains a resilient WebSocket connection and dispatches
/// inbound messages to registered handlers.
///
/// Expected message envelope:
/// <code>{ "event_type": "attraction_updated", "event_data": { ... } }</code>
///
/// New event types are supported without touching this class — register a handler
/// via <see cref="On"/> and the dispatcher routes automatically.
/// </summary>
public sealed class WebSocketService : IAsyncDisposable
{
    // Backoff schedule: 2 s → 4 s → 8 s → 16 s → 30 s (cap)
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay     = TimeSpan.FromSeconds(30);

    private readonly Uri _uri;

    /// <summary>
    /// Event-type → async handler map.
    /// Keyed case-insensitively so that casing differences between producer and
    /// consumer never cause a silent dispatch miss.
    /// </summary>
    private readonly Dictionary<string, Func<JsonElement, Task>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _appCts = new();
    private Task? _connectionLoop;

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>Current connection lifecycle state.</summary>
    public WsConnectionState State { get; private set; } = WsConnectionState.Disconnected;

    /// <summary>Raised every time <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    public WebSocketService(Uri uri) => _uri = uri;

    /// <summary>
    /// Registers (or replaces) the handler invoked when a message whose
    /// <c>event_type</c> matches <paramref name="eventType"/> arrives.
    /// </summary>
    public void On(string eventType, Func<JsonElement, Task> handler) =>
        _handlers[eventType] = handler;

    /// <summary>Removes the handler for <paramref name="eventType"/>.</summary>
    public void Off(string eventType) => _handlers.Remove(eventType);

    /// <summary>
    /// Starts the background connection loop. Idempotent — subsequent calls are no-ops.
    /// </summary>
    public void Start() => _connectionLoop ??= RunConnectionLoopAsync(_appCts.Token);

    public async ValueTask DisposeAsync()
    {
        await _appCts.CancelAsync();
        if (_connectionLoop is not null)
        {
            try   { await _connectionLoop; }
            catch (OperationCanceledException) { }
        }
        _appCts.Dispose();
    }

    // ── Connection loop ───────────────────────────────────────────────────────

    private async Task RunConnectionLoopAsync(CancellationToken ct)
    {
        var delay       = InitialDelay;
        var firstAttempt = true;

        while (!ct.IsCancellationRequested)
        {
            // Wait before retrying (skip on the very first attempt)
            if (!firstAttempt)
            {
                SetState(WsConnectionState.Reconnecting);
                try   { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
                delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
            }
            firstAttempt = false;

            SetState(WsConnectionState.Connecting);
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(_uri, ct);
                delay = InitialDelay;               // reset backoff on success
                SetState(WsConnectionState.Connected);
                await ReceiveLoopAsync(socket, ct); // blocks until drop/close
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // clean shutdown
            }
            catch
            {
                // Connection failed or dropped — outer loop will wait then retry
            }
        }

        SetState(WsConnectionState.Disconnected);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            // Accumulate a potentially multi-frame message into one MemoryStream.
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            DispatchMessage(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    // ── Message dispatch ──────────────────────────────────────────────────────

    private void DispatchMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("event_type", out var typeProp) ||
                typeProp.GetString() is not string eventType)
                return;

            if (!root.TryGetProperty("event_data", out var dataProp))
                return;

            if (!_handlers.TryGetValue(eventType, out var handler))
                return;

            // Clone the JsonElement before the owning JsonDocument is disposed,
            // so the async handler can safely read it after this method returns.
            _ = handler(dataProp.Clone());
        }
        catch (JsonException) { /* silently discard malformed frames */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetState(WsConnectionState newState)
    {
        if (State == newState) return;
        State = newState;
        StateChanged?.Invoke();
    }
}

