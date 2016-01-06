using System.Collections.Generic;
using Hik.Communication.Scs.Communication.Messages;
using Hik.Communication.Scs.Communication.Protocols;
using LibFaolan.Other;

namespace LibFaolan.Network
{
    public sealed class ConanWireProtocol : IScsWireProtocol
    {
        private byte[] _backBuffer;
        private ConanStream _stream;

        public IEnumerable<IScsMessage> CreateMessages(byte[] receivedBytes)
        {
            var messages = new List<IScsMessage>();

            // zlib compression (0x80000005) (Client sends start packet, but does not use compression?)
            if (receivedBytes[0] == 0x80 && receivedBytes[1] == 0x00 &&
                receivedBytes[2] == 0x00 && receivedBytes[3] == 0x05)
            {
                //Console.WriteLine("Skipping zlib packet");
                return messages;
            }

            if (_backBuffer != null)
            {
                _stream = new PacketStream();
                _stream.Write(_backBuffer, 0, _backBuffer.Length);
                _stream.Write(receivedBytes, 0, receivedBytes.Length);
                _backBuffer = null;
            }
            else
                _stream = new ConanStream(receivedBytes);

            while (ReadMessage(messages)) ;
            return messages;
        }

        public byte[] GetBytes(IScsMessage message)
        {
            return ((SendPacket) message).Data;
        }

        public void Reset()
        {
            //throw new NotImplementedException();
        }

        private bool ReadMessage(ICollection<IScsMessage> messages)
        {
            if (_stream == null || _stream.Length == 0)
                return false;

            _stream.Position = 0;

            var packet = new ConanPacket(_stream);
            if (packet.Length != uint.MaxValue)
            {
                messages.Add(packet);
                Functions.TrimStream(ref _stream);
                return true;
            }

            _backBuffer = _stream.ToArray();
            return false;
        }
    }
}