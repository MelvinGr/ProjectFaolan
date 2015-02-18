using System;
using LibFaolan.Extentions;
using LibFaolan.Network.Shared;

namespace LibFaolan.Network.Internal
{
    public class InternalPacket : Packet
    {
        /*
            uint32 length
            uint32 crc32
            uint8 sender
            uint8 receiver
            uint16 opcode
        */

        public const UInt32 Headersize = sizeof (UInt32) + sizeof (byte)*2 + sizeof (UInt16);
        public UInt32 Crc32;
        public PacketBuffer Data;
        public UInt16 Opcode;
        public byte Receiver;
        public byte Sender;

        public InternalPacket(PacketBuffer stream)
            : base(stream)
        {
            Length = PacketBuffer.ReadUInt32();

            if (PacketBuffer.Length < Length)
                throw new Exception("stream.Length < length");

            Crc32 = PacketBuffer.ReadUInt32();
            Sender = PacketBuffer.ReadByte();
            Receiver = PacketBuffer.ReadByte();
            Opcode = PacketBuffer.ReadUInt16();

            var dataLength = Length - Headersize;
            if (dataLength > 0)
                Data = new PacketBuffer(PacketBuffer.ReadArray(dataLength));
        }

        public override string ToString()
        {
            return string.Format("Packet with Opcode: {0} Length: {1}", Opcode.ToHex(), PacketBuffer.Length);
        }
    }
}