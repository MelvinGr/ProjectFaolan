using System;
using Hik.Communication.Scs.Communication.Messages;
using LibFaolan.Network;

namespace AgentServer
{
    public class AgentServerPacket : IScsMessage
    {
        private readonly ConanStream _originalStream;
        public ConanStream Data;
        public UInt16 Length;
        public UInt16 Opcode;

        public AgentServerPacket(ConanStream stream)
        {
            _originalStream = stream;
            stream.Position = 0;

            Opcode = stream.ReadUInt16();
            Length = stream.ReadUInt16();

            if (Length == 0 || stream.Length < Length)
            {
                //throw new Exception("stream.Length < length");
                Length = UInt16.MaxValue;
                return;
            }

            Data = new ConanStream(stream.ReadArray(Length - sizeof (UInt16)*2u));

            Console.WriteLine();
        }

        public string MessageId => null;

        public string RepliedMessageId
        {
            get { return null; }
            set { }
        }
    }
}