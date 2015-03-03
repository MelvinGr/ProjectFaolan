using System;
using Hik.Communication.Scs.Communication.Messages;
using LibFaolan.Extentions;

namespace LibFaolan.Network
{
    public sealed class Packet : IScsMessage
    {
        /////////
        /*
            UInt32 length;
            UInt32 crc32;
            UInt32 headersize;
            byte sender;
            byte slength;
            byte[] senderInt;
            byte receiver;
            byte rlength;
            byte[] receiverInt;
            UInt32 opcode;
            byte[] headerData;
            byte[] data; 
        */

        private readonly ConanStream originalStream;
        public UInt32 Crc32;
        public ConanStream Data;
        public ConanStream HeaderData;
        public UInt32 Headersize;
        public UInt32 Length;
        public UInt32 Opcode;
        public byte Receiver;
        public byte[] ReceiverInt;
        public byte Sender;
        public byte[] SenderInt;

        public Packet(ConanStream stream)
        {
            originalStream = stream;
            stream.Position = 0;

            Length = stream.ReadUInt32();

            if (Length == 0 || stream.Length < Length)
            {
                //throw new Exception("stream.Length < length");
                Length = UInt32.MaxValue;
                return;
            }

            Crc32 = stream.ReadUInt32();
            Headersize = stream.ReadUInt32();
            if (Headersize > 0xffff) // this should do..
            {
                //throw new Exception("Headersize > 0xffff");
                Length = UInt32.MaxValue;
                return;
            }

            Sender = stream.ReadByte();
            SenderInt = stream.ReadArrayPrependLengthByte();

            Receiver = stream.ReadByte();
            ReceiverInt = stream.ReadArrayPrependLengthByte();

            Opcode = stream.ReadUInt16();

            var checkHeaderLength = Headersize - (UInt32) (sizeof (UInt32) + SenderInt.Length + ReceiverInt.Length);
            if (checkHeaderLength > 0)
                HeaderData = new ConanStream(stream.ReadArray(checkHeaderLength - sizeof (byte)*2));

            var dataLength = Length - (Headersize + sizeof (UInt32)*2);
            if (dataLength > 0)
                Data = new ConanStream(stream.ReadArray(dataLength));
        }

        public UInt32 HeaderLength => (Headersize + sizeof (UInt32)*2); // + length & crc32
        public string MessageId => null;

        public string RepliedMessageId
        {
            get { return null; }
            set { }
        }

        public override string ToString()
        {
            return string.Format("Opcode: {0} Length: {1}", Opcode.ToHex(), originalStream.Length);
        }
    }
}