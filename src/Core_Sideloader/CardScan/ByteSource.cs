using System;
using System.IO;

namespace KKLazyZipmods.CardScan
{
    /// <summary>
    /// Minimal random-access byte source used by the MessagePack reader.
    /// Implemented as a buffered FileStream wrapper (net35 compatible, no Span / no memory mapping).
    /// </summary>
    internal abstract class ByteSource : IDisposable
    {
        public abstract long Length { get; }
        public abstract long Position { get; set; }
        public abstract int ReadByte();
        public abstract int Read(byte[] buffer, int offset, int count);

        public void ReadExact(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = Read(buffer, offset + total, count - total);
                if (n <= 0)
                    throw new EndOfStreamException("Unexpected end of data.");
                total += n;
            }
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            byte[] buffer = new byte[count];
            ReadExact(buffer, 0, count);
            return buffer;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    internal sealed class MemorySource : ByteSource
    {
        private readonly byte[] _data;
        private readonly int _origin;
        private readonly int _count;
        private int _position;

        public MemorySource(byte[] data) : this(data, 0, data == null ? 0 : data.Length)
        {
        }

        public MemorySource(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            _data = data;
            _origin = offset;
            _count = count;
        }

        public override long Length
        {
            get { return _count; }
        }

        public override long Position
        {
            get { return _position; }
            set
            {
                if (value < 0 || value > _count)
                    throw new ArgumentOutOfRangeException("value");
                _position = (int)value;
            }
        }

        public override int ReadByte()
        {
            if (_position >= _count)
                return -1;
            return _data[_origin + _position++];
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = _count - _position;
            if (available <= 0)
                return 0;
            if (count > available)
                count = available;
            Buffer.BlockCopy(_data, _origin + _position, buffer, offset, count);
            _position += count;
            return count;
        }
    }

    internal sealed class BufferedFileSource : ByteSource
    {
        private const int DefaultBufferSize = 1 << 20; // 1 MiB

        private readonly FileStream _stream;
        private readonly byte[] _buffer;
        private long _bufferStart = -1;
        private int _bufferLength;
        private long _position;

        public BufferedFileSource(string path)
            : this(path, DefaultBufferSize)
        {
        }

        public BufferedFileSource(string path, int bufferSize)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (bufferSize < 4096)
                bufferSize = 4096;
            _buffer = new byte[bufferSize];
        }

        public override long Length
        {
            get { return _stream.Length; }
        }

        public override long Position
        {
            get { return _position; }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException("value");
                _position = value;
            }
        }

        private void Fill(long position)
        {
            if (_bufferStart >= 0 && position >= _bufferStart && position < _bufferStart + _bufferLength)
                return;
            if (position >= _stream.Length)
            {
                _bufferStart = position;
                _bufferLength = 0;
                return;
            }
            _stream.Position = position;
            int want = _buffer.Length;
            long remaining = _stream.Length - position;
            if (want > remaining)
                want = (int)remaining;
            int total = 0;
            while (total < want)
            {
                int n = _stream.Read(_buffer, total, want - total);
                if (n <= 0)
                    break;
                total += n;
            }
            _bufferStart = position;
            _bufferLength = total;
        }

        public override int ReadByte()
        {
            if (_position >= _stream.Length)
                return -1;
            Fill(_position);
            long delta = _position - _bufferStart;
            if (delta < 0 || delta >= _bufferLength)
                return -1;
            _position++;
            return _buffer[(int)delta];
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
                return 0;
            if (_position >= _stream.Length)
                return 0;
            Fill(_position);
            long delta = _position - _bufferStart;
            int available = _bufferLength - (int)delta;
            if (available <= 0)
                return 0;
            if (count > available)
                count = available;
            Buffer.BlockCopy(_buffer, (int)delta, buffer, offset, count);
            _position += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _stream != null)
                _stream.Dispose();
        }
    }
}
