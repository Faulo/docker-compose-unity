using System.Buffers.Binary;
using System.Text;

namespace ComposeUnity.Tests;

public sealed class DockerStreamTests {
    [Fact]
    public void NormalizesUppercaseWindowsHostnameForDockerInspection() {
        var candidates = DockerEngineClient.SelfInspectionCandidates(null, "84A2225C9DAC");

        Assert.Equal(["84a2225c9dac", "84A2225C9DAC"], candidates);
    }

    [Fact]
    public async Task SeparatesAndOrdersMultiplexedDockerFrames() {
        await using var stream = new MemoryStream();
        WriteFrame(stream, 1, "first");
        WriteFrame(stream, 1, " second");
        WriteFrame(stream, 2, "problem");
        WriteFrame(stream, 1, "last");
        stream.Position = 0;

        var output = await DockerEngineClient.ReadMultiplexedAsync(stream, CancellationToken.None);

        Assert.Equal("first secondlast", output.standardOutput);
        Assert.Equal("problem", output.standardError);
        Assert.Equal("[stdout]\nfirst second\n[stderr]\nproblem\n[stdout]\nlast", output.combinedOutput);
    }

    [Fact]
    public async Task AcceptsUnframedOutputAsStandardOutput() {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("plain output"));

        var output = await DockerEngineClient.ReadMultiplexedAsync(stream, CancellationToken.None);

        Assert.Equal("plain output", output.standardOutput);
        Assert.Empty(output.standardError);
        Assert.Equal("[stdout]\nplain output", output.combinedOutput);
    }

    [Fact]
    public async Task RejectsTruncatedDockerFrame() {
        await using var stream = new MemoryStream();
        WriteFrame(stream, 1, "short", declaredLength: 10);
        stream.Position = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await DockerEngineClient.ReadMultiplexedAsync(stream, CancellationToken.None));
    }

    static void WriteFrame(Stream stream, byte streamType, string value, int? declaredLength = null) {
        byte[] content = Encoding.UTF8.GetBytes(value);
        Span<byte> header = stackalloc byte[8];
        header[0] = streamType;
        BinaryPrimitives.WriteInt32BigEndian(header[4..], declaredLength ?? content.Length);
        stream.Write(header);
        stream.Write(content);
    }
}
