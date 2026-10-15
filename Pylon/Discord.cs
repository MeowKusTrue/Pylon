using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Pylon;

/// <summary>
/// Discord Rich Presence ("Listening to …" in your Discord profile) over Discord's local pipe: no SDK, no network.
/// Frames are [int32 opcode][int32 length][JSON]; opcode 0 is the handshake, 1 a command.
/// </summary>
public sealed class Discord : IDisposable
{
    /// <summary>The Pylon application's id from discord.com/developers (public; images "pylon" uploaded there). Empty = off.</summary>
    public const string ClientId = "";

    public static bool Possible => ClientId.Length > 0;

    NamedPipeClientStream? _pipe;
    readonly SemaphoreSlim _gate = new(1);

    /// <summary>Shows the track; null clears it. Quietly does nothing when Discord isn't running.</summary>
    public async Task SetAsync(string? title, string? artist, bool audio, long positionMs, long lengthMs, bool playing)
    {
        if (!Possible) return;
        await _gate.WaitAsync();
        try
        {
            if (!await ConnectAsync()) return;
            object? activity = title == null ? null : new
            {
                type = audio ? 2 : 3, // listening / watching
                details = Trim(title),
                state = Trim(artist ?? (audio ? "Pylon" : "Pylon")),
                timestamps = playing && lengthMs > 0
                    ? new { start = DateTimeOffset.UtcNow.AddMilliseconds(-positionMs).ToUnixTimeMilliseconds(), end = DateTimeOffset.UtcNow.AddMilliseconds(lengthMs - positionMs).ToUnixTimeMilliseconds() }
                    : null,
                assets = new { large_image = "pylon", large_text = "Pylon", small_image = playing ? "play" : "pause" },
            };
            await SendAsync(1, new { cmd = "SET_ACTIVITY", nonce = Guid.NewGuid().ToString(), args = new { pid = Environment.ProcessId, activity } });
        }
        catch (IOException) { Close(); } // Discord quit: try again on the next update
        finally { _gate.Release(); }
    }

    static string Trim(string s) => s.Length > 120 ? s[..120] : s.Length < 2 ? s + " " : s; // Discord's limits: 2..128

    async Task<bool> ConnectAsync()
    {
        if (_pipe is { IsConnected: true }) return true;
        Close();
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(150); }
            catch (Exception e) when (e is TimeoutException or IOException) { pipe.Dispose(); continue; }
            _pipe = pipe;
            await SendAsync(0, new { v = 1, client_id = ClientId });
            await ReadAsync(); // READY
            return true;
        }
        return false;
    }

    async Task SendAsync(int op, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var frame = new byte[8 + json.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), json.Length);
        json.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame);
        await ReadAsync(); // every command is answered; reading keeps the pipe from filling up
    }

    async Task ReadAsync()
    {
        var head = new byte[8];
        await _pipe!.ReadExactlyAsync(head);
        var body = new byte[BitConverter.ToInt32(head, 4)];
        await _pipe.ReadExactlyAsync(body);
    }

    void Close()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose() => Close();
}
