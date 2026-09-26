namespace Recoil.Zbd.Mcp;

/// <summary>Bounds each newline-delimited JSON-RPC request before the SDK parses it.</summary>
internal sealed class BoundedMcpStream(Stream inner) : Stream
{
    private int lineBytes;
    private void Check(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value == (byte)'\n') lineBytes = 0;
            else if (++lineBytes > 2 * 1024 * 1024) throw new IOException("MCP request exceeds 2 MiB.");
        }
    }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { int read = inner.Read(buffer, offset, count); Check(buffer.AsSpan(offset, read)); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { int read = await inner.ReadAsync(buffer, cancellationToken); Check(buffer.Span[..read]); return read; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
