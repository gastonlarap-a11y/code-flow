using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace CodeFlow.Ipc;

/// <summary>
/// Reads and writes length-prefixed frames over a duplex byte stream.
/// </summary>
/// <remarks>
/// <para>
/// Wire format: a 4-byte little-endian unsigned length followed by that many bytes of UTF-8 JSON.
/// Written once here and shared by both platforms — only how the underlying stream is created
/// differs (a named pipe on Windows, a Unix domain socket on macOS).
/// </para>
/// <para>
/// <see cref="System.IO.Pipelines"/> rather than hand-rolled buffers: partial reads across frame
/// boundaries are the classic source of framing bugs, and <c>PipeReader</c> already solves
/// buffering, backpressure and multi-segment sequences.
/// </para>
/// </remarks>
public static class FrameCodec
{
    /// <summary>Header size in bytes: one little-endian <see cref="uint"/>.</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// Largest frame accepted, as a guard against a corrupt or hostile length prefix.
    /// </summary>
    /// <remarks>
    /// 64 MiB is far above any real payload — the biggest are whole diffs and HTTP response
    /// bodies — but small enough that a garbage length cannot make this process allocate until it
    /// dies. A frame beyond it is a protocol error, not something to grow a buffer for.
    /// </remarks>
    public const int MaxFrameSize = 64 * 1024 * 1024;

    /// <summary>Reads one frame, or returns <see langword="null"/> when the peer closed the connection.</summary>
    /// <exception cref="InvalidDataException">The length prefix is implausible.</exception>
    public static async ValueTask<byte[]?> ReadFrameAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = read.Buffer;

            if (TryParseFrame(ref buffer, out var frame))
            {
                reader.AdvanceTo(buffer.Start, buffer.Start);
                return frame;
            }

            // Not enough bytes yet: mark everything examined so the next read waits for more
            // rather than spinning on the same incomplete buffer.
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (read.IsCompleted)
            {
                return read.Buffer.IsEmpty
                    ? null
                    : throw new InvalidDataException(
                        $"connection closed mid-frame with {read.Buffer.Length} bytes buffered");
            }
        }
    }

    /// <summary>Writes one frame and flushes it.</summary>
    public static async ValueTask WriteFrameAsync(
        PipeWriter writer,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaxFrameSize)
        {
            throw new InvalidDataException(
                $"frame of {payload.Length} bytes exceeds the {MaxFrameSize} byte limit");
        }

        var span = writer.GetSpan(HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)payload.Length);
        writer.Advance(HeaderSize);
        writer.Write(payload.Span);

        var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCompleted)
        {
            throw new IOException("the peer closed the connection while a frame was being written");
        }
    }

    private static bool TryParseFrame(ref ReadOnlySequence<byte> buffer, out byte[]? frame)
    {
        frame = null;
        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);

        if (length > MaxFrameSize)
        {
            throw new InvalidDataException(
                $"frame length {length} exceeds the {MaxFrameSize} byte limit — the stream is out of sync");
        }

        if (buffer.Length < HeaderSize + length)
        {
            return false;
        }

        frame = buffer.Slice(HeaderSize, length).ToArray();
        buffer = buffer.Slice(HeaderSize + length);
        return true;
    }
}
