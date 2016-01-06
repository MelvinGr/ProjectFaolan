using Hik.Communication.Scs.Communication.Messages;
using LibFaolan.Extentions;

namespace LibFaolan.Network
{
    public class ConanPacket : IScsMessage
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

        private readonly ConanStream _originalStream;
        public uint Crc32;
        public ConanStream Data;
        public ConanStream HeaderData;
        public uint Headersize;
        public uint Length;
        public uint Opcode;
        public byte Receiver;
        public byte[] ReceiverInt;
        public byte Sender;
        public byte[] SenderInt;

        public ConanPacket(ConanStream stream)
        {
            _originalStream = stream;
            stream.Position = 0;

            Length = stream.ReadUInt32();

            if (Length == 0 || stream.Length < Length)
            {
                //throw new Exception("stream.Length < length");
                Length = uint.MaxValue;
                return;
            }

            Crc32 = stream.ReadUInt32();
            Headersize = stream.ReadUInt32();
            if (Headersize > 0xffff) // this should do..
            {
                //throw new Exception("Headersize > 0xffff");
                Length = uint.MaxValue;
                return;
            }

            Sender = stream.ReadByte();
            SenderInt = stream.ReadArrayPrependLengthByte();

            Receiver = stream.ReadByte();
            ReceiverInt = stream.ReadArrayPrependLengthByte();

            Opcode = stream.ReadUInt16();

            var checkHeaderLength = Headersize - (uint) (sizeof (uint) + SenderInt.Length + ReceiverInt.Length);
            if (checkHeaderLength > 0)
                HeaderData = new ConanStream(stream.ReadArray(checkHeaderLength - sizeof (byte)*2));

            var dataLength = Length - (Headersize + sizeof (uint)*2);
            if (dataLength > 0)
                Data = new ConanStream(stream.ReadArray(dataLength));
        }

        public uint HeaderLength => Headersize + sizeof (uint)*2; // + length & crc32
        public string MessageId => null;

        public string RepliedMessageId
        {
            get { return null; }
            set { }
        }

        public override string ToString()
        {
            return string.Format("Opcode: {0} Length: {1}", Opcode.ToHex(), _originalStream.Length);
        }
    }
}