using System;
using System.IO;
using System.Text;

namespace KKLazyZipmods.CardScan
{
    /// <summary>
    /// Thrown when a card payload cannot be decoded. .NET 3.5 has no InvalidDataException,
    /// so we carry our own.
    /// </summary>
    internal sealed class CardScanException : Exception
    {
        public CardScanException(string message) : base(message) { }
    }

    /// <summary>
    /// Subset of MessagePack value types. Only the types that can appear in an
    /// ExtensibleSaveFormat payload are modelled; everything else is rejected.
    /// </summary>
    internal enum MsgPackType
    {
        Nil,
        Boolean,
        Integer,
        Float,
        String,
        Binary,
        Array,
        Map,
        Extension
    }

    /// <summary>
    /// Streaming, forward-only MessagePack reader that works directly on a
    /// <see cref="ByteSource"/>. No allocation for skipped values: skipping is a
    /// seek, so a 47 MB plugin blob costs almost nothing to step over.
    /// </summary>
    internal sealed class MsgPackReader
    {
        private const int MaxDepth = 64;

        private readonly ByteSource _source;

        private MsgPackType _type;
        private int _count;
        private int _length;
        private long _integer;
        private double _float;
        private bool _boolean;

        public MsgPackReader(ByteSource source)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            _source = source;
        }

        public ByteSource Source
        {
            get { return _source; }
        }

        public MsgPackType Type
        {
            get { return _type; }
        }

        /// <summary>Element count for arrays and maps.</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>Byte length for strings, binaries and extensions.</summary>
        public int Length
        {
            get { return _length; }
        }

        public long Integer
        {
            get { return _integer; }
        }

        public double Float
        {
            get { return _float; }
        }

        public bool Boolean
        {
            get { return _boolean; }
        }

        public long Position
        {
            get { return _source.Position; }
            set { _source.Position = value; }
        }

        public long Length2
        {
            get { return _source.Length; }
        }

        // ---------------------------------------------------------------- read

        public void ReadType()
        {
            int b = _source.ReadByte();
            if (b < 0)
                throw new EndOfStreamException("Unexpected end of MessagePack data.");

            _count = 0;
            _length = 0;

            if (b <= 0x7F)
            {
                _type = MsgPackType.Integer;
                _integer = b;
                return;
            }

            if (b >= 0xE0)
            {
                _type = MsgPackType.Integer;
                _integer = (sbyte)b;
                return;
            }

            if (b >= 0x80 && b <= 0x8F)
            {
                _type = MsgPackType.Map;
                _count = b & 0x0F;
                return;
            }

            if (b >= 0x90 && b <= 0x9F)
            {
                _type = MsgPackType.Array;
                _count = b & 0x0F;
                return;
            }

            if (b >= 0xA0 && b <= 0xBF)
            {
                _type = MsgPackType.String;
                _length = b & 0x1F;
                return;
            }

            switch (b)
            {
                case 0xC0:
                    _type = MsgPackType.Nil;
                    return;
                case 0xC1:
                    throw new CardScanException("Invalid MessagePack byte 0xC1.");
                case 0xC2:
                    _type = MsgPackType.Boolean;
                    _boolean = false;
                    return;
                case 0xC3:
                    _type = MsgPackType.Boolean;
                    _boolean = true;
                    return;
                case 0xC4:
                    _type = MsgPackType.Binary;
                    _length = ReadUInt8();
                    return;
                case 0xC5:
                    _type = MsgPackType.Binary;
                    _length = ReadUInt16();
                    return;
                case 0xC6:
                    _type = MsgPackType.Binary;
                    _length = ReadLength32();
                    return;
                case 0xC7:
                    _type = MsgPackType.Extension;
                    _length = ReadUInt8();
                    ReadExtTypeByte();
                    return;
                case 0xC8:
                    _type = MsgPackType.Extension;
                    _length = ReadUInt16();
                    ReadExtTypeByte();
                    return;
                case 0xC9:
                    _type = MsgPackType.Extension;
                    _length = ReadLength32();
                    ReadExtTypeByte();
                    return;
                case 0xCA:
                    _type = MsgPackType.Float;
                    _float = ReadSingle();
                    return;
                case 0xCB:
                    _type = MsgPackType.Float;
                    _float = ReadDouble();
                    return;
                case 0xCC:
                    _type = MsgPackType.Integer;
                    _integer = ReadUInt8();
                    return;
                case 0xCD:
                    _type = MsgPackType.Integer;
                    _integer = ReadUInt16();
                    return;
                case 0xCE:
                    _type = MsgPackType.Integer;
                    _integer = ReadUInt32();
                    return;
                case 0xCF:
                    _type = MsgPackType.Integer;
                    _integer = (long)ReadUInt64();
                    return;
                case 0xD0:
                    _type = MsgPackType.Integer;
                    _integer = (sbyte)ReadUInt8();
                    return;
                case 0xD1:
                    _type = MsgPackType.Integer;
                    _integer = (short)ReadUInt16();
                    return;
                case 0xD2:
                    _type = MsgPackType.Integer;
                    _integer = (int)ReadUInt32();
                    return;
                case 0xD3:
                    _type = MsgPackType.Integer;
                    _integer = (long)ReadUInt64();
                    return;
                case 0xD4:
                    _type = MsgPackType.Extension;
                    _length = 1;
                    ReadExtTypeByte();
                    return;
                case 0xD5:
                    _type = MsgPackType.Extension;
                    _length = 2;
                    ReadExtTypeByte();
                    return;
                case 0xD6:
                    _type = MsgPackType.Extension;
                    _length = 4;
                    ReadExtTypeByte();
                    return;
                case 0xD7:
                    _type = MsgPackType.Extension;
                    _length = 8;
                    ReadExtTypeByte();
                    return;
                case 0xD8:
                    _type = MsgPackType.Extension;
                    _length = 16;
                    ReadExtTypeByte();
                    return;
                case 0xD9:
                    _type = MsgPackType.String;
                    _length = ReadUInt8();
                    return;
                case 0xDA:
                    _type = MsgPackType.String;
                    _length = ReadUInt16();
                    return;
                case 0xDB:
                    _type = MsgPackType.String;
                    _length = ReadLength32();
                    return;
                case 0xDC:
                    _type = MsgPackType.Array;
                    _count = ReadUInt16();
                    return;
                case 0xDD:
                    _type = MsgPackType.Array;
                    _count = ReadLength32();
                    return;
                case 0xDE:
                    _type = MsgPackType.Map;
                    _count = ReadUInt16();
                    return;
                case 0xDF:
                    _type = MsgPackType.Map;
                    _count = ReadLength32();
                    return;
                default:
                    throw new CardScanException("Unsupported MessagePack marker 0x" + b.ToString("X2") + ".");
            }
        }

        /// <summary>Reads the payload of a String value (call after ReadType).</summary>
        public string ReadStringValue()
        {
            if (_type != MsgPackType.String)
                throw new CardScanException("Expected a MessagePack string.");
            if (_length == 0)
                return string.Empty;
            byte[] bytes = _source.ReadBytes(_length);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Reads the payload of a Binary value (call after ReadType).</summary>
        public byte[] ReadBinaryValue()
        {
            if (_type != MsgPackType.Binary)
                throw new CardScanException("Expected a MessagePack binary.");
            if (_length == 0)
                return new byte[0];
            return _source.ReadBytes(_length);
        }

        public void SkipValue()
        {
            SkipValue(0);
        }

        private void SkipValue(int depth)
        {
            if (depth > MaxDepth)
                throw new CardScanException("MessagePack nesting is too deep.");

            switch (_type)
            {
                case MsgPackType.Nil:
                case MsgPackType.Boolean:
                case MsgPackType.Integer:
                case MsgPackType.Float:
                    return;

                case MsgPackType.String:
                case MsgPackType.Binary:
                case MsgPackType.Extension:
                    SkipBytes(_length);
                    return;

                case MsgPackType.Array:
                {
                    int n = _count;
                    for (int i = 0; i < n; i++)
                    {
                        ReadType();
                        SkipValue(depth + 1);
                    }
                    return;
                }

                case MsgPackType.Map:
                {
                    int n = _count;
                    for (int i = 0; i < n; i++)
                    {
                        ReadType();
                        SkipValue(depth + 1);
                        ReadType();
                        SkipValue(depth + 1);
                    }
                    return;
                }

                default:
                    throw new CardScanException("Unsupported MessagePack type.");
            }
        }

        public void SkipBytes(long count)
        {
            if (count < 0)
                throw new CardScanException("Negative skip.");
            long target = _source.Position + count;
            if (target > _source.Length)
                throw new EndOfStreamException("Skip past end of data.");
            _source.Position = target;
        }

        // ------------------------------------------------------------ helpers

        private void ReadExtTypeByte()
        {
            // The ext "type" byte is signed but we do not care about its value.
            if (_source.ReadByte() < 0)
                throw new EndOfStreamException("Unexpected end of MessagePack data.");
        }

        private int ReadUInt8()
        {
            int b = _source.ReadByte();
            if (b < 0)
                throw new EndOfStreamException("Unexpected end of MessagePack data.");
            return b;
        }

        private int ReadUInt16()
        {
            int hi = ReadUInt8();
            int lo = ReadUInt8();
            return (hi << 8) | lo;
        }

        private long ReadUInt32()
        {
            long hi = ReadUInt16();
            long lo = ReadUInt16();
            return (hi << 16) | lo;
        }

        private ulong ReadUInt64()
        {
            ulong hi = (ulong)ReadUInt32();
            ulong lo = (ulong)ReadUInt32();
            return (hi << 32) | lo;
        }

        private int ReadLength32()
        {
            long value = ReadUInt32();
            if (value > int.MaxValue)
                throw new CardScanException("MessagePack length is too large.");
            return (int)value;
        }

        private float ReadSingle()
        {
            byte[] bytes = _source.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        private double ReadDouble()
        {
            byte[] bytes = _source.ReadBytes(8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }
    }
}
