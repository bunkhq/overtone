using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Overtone.Platform;

/// <summary>
/// Minimal Discord RPC client over the local IPC pipe (\\.\pipe\discord-ipc-N).
/// Frames are an 8-byte header (opcode, length, both little-endian int32) plus
/// UTF-8 JSON. Only what Rich Presence needs: handshake, SET_ACTIVITY, ping/pong.
/// </summary>
public sealed class DiscordIpc : IDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

    private readonly string _clientId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public DiscordIpc(string clientId) => _clientId = clientId;

    /// <summary>Raised (on a pool thread) when Discord closes the pipe or rejects the client.</summary>
    public event Action<string>? Disconnected;

    public bool Connected => _pipe?.IsConnected == true;

    /// <summary>Connects to the first running Discord client and completes the handshake.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10 && _pipe is null; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await p.ConnectAsync(250, ct);
                _pipe = p;
            }
            catch (Exception)
            {
                await p.DisposeAsync();
            }
        }
        if (_pipe is null) return false;

        try
        {
            await WriteAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = _clientId }, ct);
            var (op, body) = await ReadAsync(_pipe, ct);
            if (op != OpFrame || body?["evt"]?.GetValue<string>() != "READY")
            {
                // An unknown application ID comes back as a close frame with the reason.
                Drop($"handshake rejected: {body?.ToJsonString()}");
                return false;
            }
            _ = ReadLoopAsync(_pipe);
            return true;
        }
        catch (Exception ex)
        {
            Drop(ex.Message);
            return false;
        }
    }

    /// <summary>Sets (or with null, clears) this process's activity.</summary>
    public Task SetActivityAsync(JsonObject? activity, CancellationToken ct = default) =>
        WriteAsync(OpFrame, new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            ["nonce"] = Guid.NewGuid().ToString("N"),
        }, ct);

    private async Task WriteAsync(int op, JsonNode payload, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new IOException("not connected");
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<(int op, JsonNode? body)> ReadAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (len is < 0 or > 1 << 20) throw new IOException($"bad frame length {len}");
        var body = new byte[len];
        await pipe.ReadExactlyAsync(body, ct);
        return (op, len == 0 ? null : JsonNode.Parse(body));
    }

    /// <summary>Drains Discord's replies (the pipe stalls if nobody reads) and answers pings.</summary>
    private async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        try
        {
            while (pipe.IsConnected)
            {
                var (op, body) = await ReadAsync(pipe, CancellationToken.None);
                var evt = body is JsonObject o && o.TryGetPropertyValue("evt", out var e) ? e?.ToString() : null;
                if (op == OpPing)
                {
                    await WriteAsync(OpPong, body ?? new JsonObject(), CancellationToken.None);
                }
                else if (op == OpClose)
                {
                    Drop($"closed by Discord: {body?.ToJsonString()}");
                    return;
                }
                else if (op == OpFrame && evt == "ERROR")
                {
                    Settings.LogError(new Exception($"Discord RPC error: {body?.ToJsonString()}"));
                }
            }
        }
        catch (Exception ex)
        {
            if (_pipe == pipe) Drop(ex.Message);
        }
    }

    private void Drop(string reason)
    {
        var pipe = Interlocked.Exchange(ref _pipe, null);
        if (pipe is null) return;
        try { pipe.Dispose(); } catch { }
        Disconnected?.Invoke(reason);
    }

    public void Dispose() => Drop("disposed");
}
