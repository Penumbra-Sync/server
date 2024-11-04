using System.Globalization;
using System.Text;

namespace MareSynchronosStaticFilesServer.Utils;

public sealed class BlockFileDataSubstream : IDisposable
{
    private readonly MemoryStream _headerStream;
    private bool _disposed = false;
    private readonly Lazy<FileStream> _dataStreamLazy;
    private FileStream DataStream => _dataStreamLazy.Value;

    public BlockFileDataSubstream(FileInfo file)
    {
        _dataStreamLazy = new(() => File.Open(file.FullName, GetFileStreamOptions(file.Length)));
        _headerStream = new MemoryStream(Encoding.ASCII.GetBytes("#" + file.Name + ":" + file.Length.ToString(CultureInfo.InvariantCulture) + "#"));
    }

    private static FileStreamOptions GetFileStreamOptions(long fileSize)
    {
        int bufferSize = fileSize switch
        {
            <= 128 * 1024 => 0,
            <= 512 * 1024 => 4096,
            <= 1 * 1024 * 1024 => 65536,
            <= 10 * 1024 * 1024 => 131072,
            <= 100 * 1024 * 1024 => 524288,
            _ => 1048576
        };

        FileStreamOptions opts = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Inheritable,
            BufferSize = bufferSize
        };

        return opts;
    }

    public int Read(byte[] inputBuffer, int offset, int count)
    {
        int bytesRead = 0;

        // Read from header stream if it has remaining data
        if (_headerStream.Position < _headerStream.Length)
        {
            int headerBytesToRead = (int)Math.Min(count, _headerStream.Length - _headerStream.Position);
            bytesRead += _headerStream.Read(inputBuffer, offset, headerBytesToRead);
            offset += bytesRead;
            count -= bytesRead;
        }

        // Read from data stream if there is still space in buffer
        if (count > 0 && DataStream.Position < DataStream.Length)
        {
            bytesRead += DataStream.Read(inputBuffer, offset, count);
        }

        return bytesRead;
    }

    public async Task<int> ReadAsync(byte[] inputBuffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        int bytesRead = 0;

        // Async read from header stream
        if (_headerStream.Position < _headerStream.Length)
        {
            int headerBytesToRead = (int)Math.Min(count, _headerStream.Length - _headerStream.Position);
            bytesRead += await _headerStream.ReadAsync(inputBuffer.AsMemory(offset, headerBytesToRead), cancellationToken).ConfigureAwait(false);
            offset += bytesRead;
            count -= bytesRead;
        }

        // Async read from data stream
        if (count > 0 && DataStream.Position < DataStream.Length)
        {
            bytesRead += await DataStream.ReadAsync(inputBuffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        return bytesRead;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int bytesRead = 0;

        // Async read from header stream
        if (_headerStream.Position < _headerStream.Length)
        {
            int headerBytesToRead = (int)Math.Min(buffer.Length, _headerStream.Length - _headerStream.Position);
            bytesRead += await _headerStream.ReadAsync(buffer.Slice(0, headerBytesToRead), cancellationToken).ConfigureAwait(false);
            buffer = buffer.Slice(headerBytesToRead);
        }

        // Async read from data stream
        if (buffer.Length > 0 && DataStream.Position < DataStream.Length)
        {
            bytesRead += await DataStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        return bytesRead;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _headerStream.Dispose();
        if (_dataStreamLazy.IsValueCreated)
            DataStream.Dispose();
        _disposed = true;
    }
}