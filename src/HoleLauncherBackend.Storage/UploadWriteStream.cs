using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Storage;

internal sealed class UploadWriteStream(Stream destination, Action<int> recordProgress) : Stream
{
    public long BytesWritten { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => destination.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckSize(buffer.Length);
        await destination.WriteAsync(buffer, cancellationToken);
        OnWritten(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckSize(count);
        destination.Write(buffer, offset, count);
        OnWritten(count);
    }

    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void CheckSize(int count)
    {
        if (count > StorageAddress.MaxUploadBytes - BytesWritten)
            throw new BadHttpRequestException("File is too large.", StatusCodes.Status413PayloadTooLarge);
    }

    private void OnWritten(int count)
    {
        BytesWritten += count;
        recordProgress(count);
    }
}
