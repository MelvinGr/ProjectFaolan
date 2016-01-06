using System.Collections.Generic;
using Hik.Communication.Scs.Communication.Messages;
using Hik.Communication.Scs.Communication.Protocols;
using LibFaolan.Network;
using LibFaolan.Other;

namespace AgentServer
{
    public sealed class AgentServerWireProtocol : IScsWireProtocol
    {
        private byte[] _backBuffer;
        private ConanStream _stream;

        public IEnumerable<IScsMessage> CreateMessages(byte[] receivedBytes)
        {
            var messages = new List<IScsMessage>();

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

            var packet = new AgentServerPacket(_stream);
            if (packet.Length != ushort.MaxValue)
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