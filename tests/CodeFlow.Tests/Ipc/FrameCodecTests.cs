using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using CodeFlow.Ipc;
using Xunit;

namespace CodeFlow.Tests.Ipc;

/// <summary>
/// Framing is where IPC bugs are silent: a frame boundary read wrong does not throw, it just
/// hands the next layer plausible-looking garbage. These tests cover the cases a naive
/// implementation gets wrong — a frame split across reads, several frames in one read, an empty
/// payload, and a length prefix that cannot be honest.
/// </summary>
public sealed class FrameCodecTests
{
    [Fact]
    public async Task Round_trips_a_single_frame()
    {
        var payload = Encoding.UTF8.GetBytes("""{"id":1,"method":"get_status"}""");
        var pipe = new Pipe();

        await FrameCodec.WriteFrameAsync(pipe.Writer, payload, TestContext.Current.CancellationToken);
        var read = await FrameCodec.ReadFrameAsync(pipe.Reader, TestContext.Current.CancellationToken);

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Reads_several_frames_delivered_in_one_chunk()
    {
        // The shell can write two frames before this side ever reads, so a reader that assumes
        // one frame per read call loses the second one.
        var pipe = new Pipe();
        var first = Encoding.UTF8.GetBytes("first");
        var second = Encoding.UTF8.GetBytes("second");

        await FrameCodec.WriteFrameAsync(pipe.Writer, first, TestContext.Current.CancellationToken);
        await FrameCodec.WriteFrameAsync(pipe.Writer, second, TestContext.Current.CancellationToken);

        Assert.Equal(first, await FrameCodec.ReadFrameAsync(pipe.Reader, TestContext.Current.CancellationToken));
        Assert.Equal(second, await FrameCodec.ReadFrameAsync(pipe.Reader, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reassembles_a_frame_split_across_reads()
    {
        // A large diff response will not arrive in one read. This is the case that breaks a
        // hand-rolled buffer and the reason the codec sits on PipeReader.
        var pipe = new Pipe();
        var payload = Encoding.UTF8.GetBytes(new string('x', 100_000));

        var reading = FrameCodec.ReadFrameAsync(pipe.Reader, TestContext.Current.CancellationToken).AsTask();

        var header = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await pipe.Writer.WriteAsync(header, TestContext.Current.CancellationToken);
        await pipe.Writer.WriteAsync(payload.AsMemory(0, 40_000), TestContext.Current.CancellationToken);

        Assert.False(reading.IsCompleted, "the frame is incomplete, so the read must still be pending");

        await pipe.Writer.WriteAsync(payload.AsMemory(40_000), TestContext.Current.CancellationToken);

        Assert.Equal(payload, await reading);
    }

    [Fact]
    public async Task Round_trips_an_empty_payload()
    {
        var pipe = new Pipe();
        await FrameCodec.WriteFrameAsync(pipe.Writer, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        var read = await FrameCodec.ReadFrameAsync(pipe.Reader, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Empty(read);
    }

    [Fact]
    public async Task Returns_null_when_the_peer_closes_cleanly()
    {
        // The shell exiting is normal, not an error: the accept loop uses this to clean up.
        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();

        Assert.Null(await FrameCodec.ReadFrameAsync(pipe.Reader, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_when_the_peer_closes_mid_frame()
    {
        var pipe = new Pipe();
        var header = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 64);
        await pipe.Writer.WriteAsync(header, TestContext.Current.CancellationToken);
        await pipe.Writer.WriteAsync(new byte[10], TestContext.Current.CancellationToken);
        await pipe.Writer.CompleteAsync();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await FrameCodec.ReadFrameAsync(pipe.Reader, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_an_implausible_length_prefix()
    {
        // Without this guard a corrupt or hostile prefix makes the process allocate until it
        // dies, and the failure looks like memory pressure rather than a protocol error.
        var pipe = new Pipe();
        var header = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        await pipe.Writer.WriteAsync(header, TestContext.Current.CancellationToken);
        await pipe.Writer.FlushAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await FrameCodec.ReadFrameAsync(pipe.Reader, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refuses_to_write_a_frame_beyond_the_limit()
    {
        var pipe = new Pipe();
        var oversized = new byte[FrameCodec.MaxFrameSize + 1];

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await FrameCodec.WriteFrameAsync(pipe.Writer, oversized, TestContext.Current.CancellationToken));
    }
}
