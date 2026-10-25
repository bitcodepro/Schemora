namespace Schemora.Host;

internal sealed class BoundedOutputStream(Stream inner, long maximum) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set
        {
            Check(value);
            inner.Position = value;
        }
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value)
    {
        Check(value);
        inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(inner.Position + count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(inner.Position + buffer.Length);
        inner.Write(buffer);
    }

    private void Check(long value)
    {
        if (value > maximum)
            throw new AppException("DOCKER_SCRIPT_TOO_LARGE", "Сгенерированный deployment script превышает 64 MiB.", 422);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
