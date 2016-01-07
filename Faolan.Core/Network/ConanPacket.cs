using System;
using Faolan.Core.Extentions;

namespace Faolan.Core.Network
{
    public class Packet
    {
        protected readonly ConanStream OriginalStream;

        public Packet(ConanStream stream)
        {
            OriginalStream = stream;
            stream.Position = 0;
        }

        public ConanStream Data { get; protected set; }
        public uint Length { get; protected set; }

        public virtual bool Valid { get; }
    }

    public class ConanPacket : Packet
    {
        public ConanPacket(ConanStream stream)
            : base(stream)
        {
            Length = stream.ReadUInt32();

            Crc32 = stream.ReadUInt32();
            Headersize = stream.ReadUInt32();

            Sender = stream.ReadByte();
            SenderInt = stream.ReadArrayPrependLengthByte();

            Receiver = stream.ReadByte();
            ReceiverInt = stream.ReadArrayPrependLengthByte();

            Opcode = stream.ReadUInt16();

            if (!Valid)
            {
                Console.WriteLine("Invalid Packet");
                return;
            }

            var checkHeaderLength = Headersize - (uint) (sizeof(uint) + SenderInt.Length + ReceiverInt.Length);
            if (checkHeaderLength > 0)
                HeaderData = new ConanStream(stream.ReadArray(checkHeaderLength - sizeof(byte) * 2));

            var dataLength = Length - (Headersize + sizeof(uint) * 2);
            if (dataLength > 0)
                Data = new ConanStream(stream.ReadArray(dataLength));
        }

        public uint Crc32 { get; protected set; }
        public ConanStream HeaderData { get; protected set; }
        public uint Headersize { get; protected set; }
        public uint Opcode { get; protected set; }
        public byte Receiver { get; protected set; }
        public byte[] ReceiverInt { get; protected set; }
        public byte Sender { get; protected set; }
        public byte[] SenderInt { get; protected set; }

        public override bool Valid => Crc32 == Crypt.Crc32.CalculateForPacketBuffer(OriginalStream);

        public uint HeaderLength => Headersize + sizeof(uint) * 2; // + length & crc32

        public override string ToString()
        {
            return $"Opcode: {Opcode.ToHex()} Length: {OriginalStream.Length}";
        }
    }
}