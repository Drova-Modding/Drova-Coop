using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// An <see cref="INetWriter"/> over a plain stream. Uses <see cref="BinaryWriter"/>'s encoding, not LiteNetLib's,
    /// which is enough to test field order and truncation.
    /// </summary>
    internal sealed class BufferWriter(Stream stream) : INetWriter
    {
        private readonly BinaryWriter _writer = new(stream);

        /// <inheritdoc/>
        public void Put(byte value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(sbyte value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(short value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(ushort value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(int value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(uint value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(long value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(ulong value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(float value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(double value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(bool value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(string value) => _writer.Write(value);

        /// <inheritdoc/>
        public void Put(byte[] value)
        {
            _writer.Write(value.Length);
            _writer.Write(value);
        }
    }

    /// <summary>Writes a message and reads it back into a fresh instance.</summary>
    internal static class Wire
    {
        internal static T RoundTrip<T>(in T message) where T : struct, INetMessage
        {
            using MemoryStream stream = new();
            message.Write(new BufferWriter(stream));

            stream.Position = 0;
            T received = default;
            received.Read(new BufferReader(stream));

            return received;
        }
    }
}
