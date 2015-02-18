using System;
using Hik.Communication.Scs.Communication.Messages;

namespace LibFaolan.Network.Shared
{
    public class Packet : IScsMessage
    {
        protected readonly PacketBuffer PacketBuffer;
        public UInt32 Length;

        public Packet(PacketBuffer stream)
        {
            PacketBuffer = stream;
            PacketBuffer.Position = 0;
        }

        public string MessageId => null;

        public string RepliedMessageId
        {
            get { return null; }
            set { }
        }

        public byte[] ToArray()
        {
            return PacketBuffer.ToArray();
        }
    }
}