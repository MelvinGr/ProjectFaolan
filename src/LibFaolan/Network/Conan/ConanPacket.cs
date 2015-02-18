using System;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network.Shared;

namespace LibFaolan.Network
{
    public class ConanPacket : Packet
    {
        public UInt32 Crc32;
        public PacketBuffer Data;
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

        public PacketBuffer HeaderData;
        public UInt32 Headersize;
        public UInt32 Opcode;
        public byte Receiver;
        public ConanArray ReceiverInt;
        public byte Sender;
        public ConanArray SenderInt;

        public ConanPacket(PacketBuffer stream)
            : base(stream)
        {
            Length = PacketBuffer.ReadUInt32();

            if (PacketBuffer.Length < Length)
            {
                //throw new Exception("stream.Length < length");
                Length = UInt32.MaxValue;
                return;
            }

            Crc32 = PacketBuffer.ReadUInt32();
            Headersize = PacketBuffer.ReadUInt32();

            Sender = PacketBuffer.ReadByte();
            SenderInt = PacketBuffer.ReadConanArray();

            Receiver = PacketBuffer.ReadByte();
            ReceiverInt = PacketBuffer.ReadConanArray();

            Opcode = PacketBuffer.ReadUInt16();

            var checkHeaderLength = Headersize - (UInt32) (sizeof (UInt32) + SenderInt.Length + ReceiverInt.Length);
            if (checkHeaderLength > 0)
                HeaderData = new PacketBuffer(PacketBuffer.ReadArray(checkHeaderLength - sizeof (byte)*2));

            var dataLength = Length - (Headersize + sizeof (UInt32)*2);
            if (dataLength > 0)
                Data = new PacketBuffer(PacketBuffer.ReadArray(dataLength));
        }

        public UInt32 HeaderLength => (Headersize + 8);

        public override string ToString()
        {
            return string.Format("Opcode: {0} Length: {1}", Opcode.ToHex(), PacketBuffer.Length);
        }
    }
}