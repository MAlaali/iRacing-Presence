using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace IRacingPresence.Discord;

sealed class DiscordException(string message) : Exception(message);

/// Talks to the Discord desktop client over its local IPC pipe (\\.\pipe\discord-ipc-N), the same
/// channel every Rich Presence library uses, so the app needs no third-party packages.
sealed class DiscordIpc(string? pipeName = null) : IDisposable
{
    const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    const int MaxFrame = 64 * 1024;
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe is { IsConnected: true };
    public string? UserName { get; private set; }

    public async Task ConnectAsync(string clientId, CancellationToken ct)
    {
        Disconnect();
        foreach (var name in CandidatePipes())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, ct);
            }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                pipe.Dispose();
                continue;
            }

            _pipe = pipe;
            try
            {
                await SendAsync(OpHandshake, new { v = 1, client_id = clientId }, ct);
                var (op, reply) = await ReceiveAsync(ct);
                using (reply)
                {
                    var root = reply.RootElement;
                    if (op == OpFrame && Str(root, "evt") == "READY")
                    {
                        UserName = root.TryGetProperty("data", out var data) && data.TryGetProperty("user", out var user)
                            ? Str(user, "global_name") ?? Str(user, "username")
                            : null;
                        return;
                    }
                    throw new DiscordException(Str(root, "message") ?? $"Discord refused the connection (op {op}).");
                }
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
        throw new DiscordException("Discord isn't running (no IPC pipe found). Rich Presence needs the Discord desktop app.");
    }

    /// Sets the activity, or clears it when activity is null.
    public async Task SetActivityAsync(DiscordActivity? activity, CancellationToken ct)
    {
        var payload = new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = Guid.NewGuid().ToString(),
        };
        await SendAsync(OpFrame, payload, ct);
        var (op, reply) = await ReceiveAsync(ct);
        using (reply)
        {
            var root = reply.RootElement;
            if (op == OpClose)
            {
                Disconnect();
                throw new DiscordException(Str(root, "message") ?? "Discord closed the connection.");
            }
            if (Str(root, "evt") == "ERROR")
            {
                var message = root.TryGetProperty("data", out var data) ? Str(data, "message") : null;
                throw new DiscordException(message ?? "Discord rejected the activity.");
            }
        }
    }

    async Task SendAsync(int op, object payload, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new DiscordException("Not connected to Discord.");
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, DiscordActivity.JsonOptions);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        try
        {
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
        catch (IOException)
        {
            Disconnect();
            throw new DiscordException("Lost the connection to Discord.");
        }
    }

    async Task<(int Op, JsonDocument Body)> ReceiveAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReplyTimeout);
        var header = new byte[8];
        try
        {
            while (true)
            {
                var pipe = _pipe ?? throw new DiscordException("Not connected to Discord.");
                await pipe.ReadExactlyAsync(header, timeout.Token);
                int op = BinaryPrimitives.ReadInt32LittleEndian(header);
                int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (len < 0 || len > MaxFrame)
                    throw new DiscordException($"Unexpected reply from Discord ({len} bytes).");

                var body = new byte[len];
                await pipe.ReadExactlyAsync(body, timeout.Token);
                if (op == OpPing)
                {
                    await SendRawAsync(OpPong, body, timeout.Token);
                    continue;
                }
                return (op, len == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(body));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Disconnect();
            throw new DiscordException("Discord didn't answer in time.");
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            Disconnect();
            throw new DiscordException("Lost the connection to Discord.");
        }
    }

    async Task SendRawAsync(int op, byte[] body, CancellationToken ct)
    {
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame, ct);
    }

    IEnumerable<string> CandidatePipes()
    {
        if (pipeName != null)
        {
            yield return pipeName;
            yield break;
        }
        // Listing \\.\pipe\ doesn't open any pipe, so it's safe to look before connecting.
        HashSet<string> existing;
        try
        {
            existing = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            existing = [];
        }
        for (int i = 0; i < 10; i++)
        {
            var name = $"discord-ipc-{i}";
            if (existing.Count == 0 || existing.Contains(name))
                yield return name;
        }
    }

    static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public void Disconnect()
    {
        _pipe?.Dispose();
        _pipe = null;
        UserName = null;
    }

    public void Dispose() => Disconnect();
}
