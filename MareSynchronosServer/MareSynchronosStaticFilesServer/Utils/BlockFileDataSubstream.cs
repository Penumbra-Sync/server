using System.Globalization;
using System.Text;

namespace MareSynchronosStaticFilesServer.Utils;

public sealed class BlockFileDataSubstream : IDisposable
{
    private readonly MemoryStream _headerStream;
    private readonly FileStream _dataStream;
    private int _headerPosition = 0;
    private long _dataPosition = 0;
    private bool _disposed = false;

    private long RemainingHeaderLength => _headerStream.Length - _headerPosition;
    private long RemainingDataLength => _dataStream.Length - _dataPosition;

    public BlockFileDataSubstream(FileStream dataStream)
    {
        _headerStream = new MemoryStream();
        using var headerStreamWriter = new BinaryWriter(_headerStream);
        headerStreamWriter.Write(Encoding.ASCII.GetBytes("#" + new FileInfo(dataStream.Name).Name + ":" + dataStream.Length.ToString(CultureInfo.InvariantCulture) + "#"));
        headerStreamWriter.Flush();
        _headerStream.Position = 0;
        _dataStream = dataStream;
    }

    public int Read(byte[] inputBuffer, int offset, int count)
    {
        int currentOffset = offset;
        int currentCount = count;
        int readHeaderBytes = 0;

        if (RemainingHeaderLength > 0)
        {
            bool readOnlyHeader = currentCount <= RemainingHeaderLength;
            byte[] readHeaderBuffer = new byte[Math.Min(currentCount, RemainingHeaderLength)];

            readHeaderBytes = _headerStream.Read(readHeaderBuffer, 0, readHeaderBuffer.Length);
            _headerPosition += readHeaderBytes;

            Buffer.BlockCopy(readHeaderBuffer, 0, inputBuffer, currentOffset, readHeaderBytes);

            if (readOnlyHeader)
            {
                return readHeaderBytes;
            }

            currentOffset += readHeaderBytes;
            currentCount -= readHeaderBytes;
        }

        if (RemainingDataLength > 0)
        {
            byte[] readDataBuffer = new byte[currentCount];
            var readDataBytes = _dataStream.Read(readDataBuffer, 0, readDataBuffer.Length);
            _dataPosition += readDataBytes;

            Buffer.BlockCopy(readDataBuffer, 0, inputBuffer, currentOffset, readDataBytes);

            return readDataBytes + readHeaderBytes;
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _headerStream.Dispose();
        _dataStream.Dispose();
        _disposed = true;
    }
}