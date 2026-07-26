using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// An <see cref="INetWriter"/> over a plain stream, so a message can be serialized without a transport.
    ///
    /// The byte encoding is <see cref="BinaryWriter"/>'s, not the one LiteNetLib actually puts on the wire.
    /// That is deliberate and worth being clear about: these tests are about this mod's field order,
    /// truncation and round-trip fidelity, none of which depend on how a string is length-prefixed. What
    /// they cannot catch is a mismatch inside the API's own writer, which is the API's own to test.
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
}
