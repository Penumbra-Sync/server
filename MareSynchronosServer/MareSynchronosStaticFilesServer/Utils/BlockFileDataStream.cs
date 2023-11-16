namespace MareSynchronosStaticFilesServer.Utils;

public sealed class BlockFileDataStream : Stream
{
    private readonly BlockFileDataSubstream[] _substreams;
    private int _currentStreamIndex = 0;

    public BlockFileDataStream(BlockFileDataSubstream[] substreams)
    {
        _substreams = substreams;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        int currentOffset = 0;
        int remainingCount = count;
        while (totalRead < count && _currentStreamIndex < _substreams.Length)
        {
            var lastReadBytes = _substreams[_currentStreamIndex].Read(buffer, currentOffset, remainingCount);
            if (lastReadBytes < remainingCount)
            {
                _substreams[_currentStreamIndex].Dispose();
                _currentStreamIndex++;
            }

            totalRead += lastReadBytes;
            currentOffset += lastReadBytes;
            remainingCount -= lastReadBytes;
        }

        return totalRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var substream in _substreams)
            {
                // probably unnecessary but better safe than sorry
                substream.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
