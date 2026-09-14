using System.Text;
using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// The counterpart to <see cref="BufferWriter"/>: an <see cref="INetReader"/> over the same stream.
    /// </summary>
    internal sealed class BufferReader(Stream stream) : INetReader
    {
        private readonly BinaryReader _reader = new(stream);

        /// <inheritdoc/>
        public byte GetByte() => _reader.ReadByte();

        /// <inheritdoc/>
        public sbyte GetSByte() => _reader.ReadSByte();

        /// <inheritdoc/>
        public short GetShort() => _reader.ReadInt16();

        /// <inheritdoc/>
        public ushort GetUShort() => _reader.ReadUInt16();

        /// <inheritdoc/>
        public int GetInt() => _reader.ReadInt32();

        /// <inheritdoc/>
        public uint GetUInt() => _reader.ReadUInt32();

        /// <inheritdoc/>
        public long GetLong() => _reader.ReadInt64();

        /// <inheritdoc/>
        public ulong GetULong() => _reader.ReadUInt64();

        /// <inheritdoc/>
        public float GetFloat() => _reader.ReadSingle();

        /// <inheritdoc/>
        public double GetDouble() => _reader.ReadDouble();

        /// <inheritdoc/>
        public bool GetBool() => _reader.ReadBoolean();

        /// <inheritdoc/>
        public string GetString() => _reader.ReadString();

        /// <inheritdoc/>
        public string GetString(int maxBytes)
        {
            // Read whole and then refused, the way the transport does it: a value over the cap yields an
            // empty string rather than a truncated one, and the reader stays in step either way.
            string value = _reader.ReadString();
            return Encoding.UTF8.GetByteCount(value) > maxBytes ? string.Empty : value;
        }

        /// <inheritdoc/>
        public byte[] GetBytes() => _reader.ReadBytes(_reader.ReadInt32());

        /// <inheritdoc/>
        public byte[] GetBytes(int maxLength)
        {
            byte[] value = _reader.ReadBytes(_reader.ReadInt32());
            return value.Length > maxLength ? Array.Empty<byte>() : value;
        }
    }
}
