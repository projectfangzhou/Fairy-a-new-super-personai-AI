using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// The minimum of RFC 6455 this project needs: read a handshake, answer it, read and write text
/// frames. Shared by the chat server and its self-test so both sides speak the same wire format.
///
/// Only what is used is implemented on purpose — no continuation frames, no binary frames, no
/// compression, no frames over <see cref="MaxPayloadBytes"/>. A client that needs more than a
/// short JSON message is out of scope rather than silently truncated.
/// </summary>
public static class WebSocketFraming
{
    public const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public const int MaxHeaderBytes = 8 * 1024;
    public const int MaxPayloadBytes = 64 * 1024;

    private const int OpcodeText = 0x1;
    private const int OpcodeClose = 0x8;
    private const int OpcodePing = 0x9;
    private const int OpcodePong = 0xA;

    /// <summary>Computes the <c>Sec-WebSocket-Accept</c> value for a client key.</summary>
    public static string Accept(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));

    /// <summary>Reads the handshake and returns the client's key, or null if it is not usable.</summary>
    public static async Task<string?> ReadHandshakeKeyAsync(NetworkStream stream,
        CancellationToken cancellationToken = default)
    {
        var text = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (text is null) return null;

        foreach (var line in text.Split("\r\n"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            if (!line[..colon].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[(colon + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    public static async Task<string?> ReadHeaderAsync(NetworkStream stream,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) return null;
            length += read;

            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }

        return null;
    }

    public static Task WriteHandshakeAsync(NetworkStream stream, string key,
        CancellationToken cancellationToken = default) =>
        WriteTextAsync(stream,
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {Accept(key)}\r\n\r\n",
            cancellationToken);

    public static Task WriteBadRequestAsync(NetworkStream stream) =>
        WriteTextAsync(stream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

    public static async Task WriteTextAsync(NetworkStream stream, string text,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one text frame. Returns null on a close frame, a dropped connection, or a frame this
    /// server refuses (binary, oversized, 64-bit length).
    /// </summary>
    public static async Task<string?> ReadTextFrameAsync(NetworkStream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;

        var opcode = header[0] & 0x0F;
        if (opcode is OpcodeClose) return null;

        var masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            var extended = new byte[2];
            if (!await ReadExactAsync(stream, extended, cancellationToken).ConfigureAwait(false)) return null;
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            return null;
        }

        if (length > MaxPayloadBytes) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask, cancellationToken).ConfigureAwait(false)) return null;

        var payload = new byte[(int)length];
        if (length > 0 && !await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
            return null;

        if (masked)
            for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

        var text = Encoding.UTF8.GetString(payload);

        if (opcode is OpcodePing)
        {
            await WriteFrameAsync(stream, text, OpcodePong, cancellationToken).ConfigureAwait(false);
            return await ReadTextFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        if (opcode is not OpcodeText) return null;

        return text;
    }

    /// <summary>Writes a text frame. Server-side frames are never masked, per RFC 6455.</summary>
    public static Task WriteFrameAsync(NetworkStream stream, string payload,
        CancellationToken cancellationToken = default) =>
        WriteFrameAsync(stream, payload, OpcodeText, cancellationToken);

    private static async Task WriteFrameAsync(NetworkStream stream, string payload, int opcode,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        byte[] frame;

        if (bytes.Length <= 125)
        {
            frame = new byte[bytes.Length + 2];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = (byte)bytes.Length;
            bytes.CopyTo(frame, 2);
        }
        else
        {
            frame = new byte[bytes.Length + 4];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = 126;
            frame[2] = (byte)(bytes.Length >> 8);
            frame[3] = (byte)(bytes.Length & 0xFF);
            bytes.CopyTo(frame, 4);
        }

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Client-side masked frame, used by the self-test and by tests.</summary>
    public static async Task WriteMaskedFrameAsync(NetworkStream stream, string payload,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > 125)
            throw new ArgumentException("客户端测试报文必须短于 126 字节。", nameof(payload));

        var mask = RandomNumberGenerator.GetBytes(4);
        var frame = new byte[bytes.Length + 6];
        frame[0] = 0x81;
        frame[1] = (byte)(0x80 | bytes.Length);
        mask.CopyTo(frame, 2);

        for (var i = 0; i < bytes.Length; i++) frame[6 + i] = (byte)(bytes[i] ^ mask[i % 4]);

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] target,
        CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = await stream.ReadAsync(target.AsMemory(offset, target.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }
}
