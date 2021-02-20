using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network
{
    public abstract class Packet
    {
        protected Packet(ConanStream stream)
        {
            Stream = stream;
            stream.Position = 0;
        }

        protected ConanStream Stream { get; }
        public ConanStream Data { get; protected init; }
        public uint Length { get; protected init; }

        public abstract bool IsValid { get; }
    }

    public class ConanPacket : Packet
    {
        public readonly uint Crc32;
        public readonly ConanStream HeaderData;
        public readonly uint HeaderLength;
        public readonly uint Opcode;
        public readonly byte Receiver;
        public readonly byte[] ReceiverBytes;
        public readonly byte Sender;
        public readonly byte[] SenderBytes;

        public ConanPacket(byte[] bytes) : base(new ConanStream(bytes))
        {
            Length = Stream.ReadUInt32();

            Crc32 = Stream.ReadUInt32();
            HeaderLength = Stream.ReadUInt32();

            Sender = Stream.ReadByte();
            SenderBytes = Stream.ReadArrayPrependLengthByte();

            Receiver = Stream.ReadByte();
            ReceiverBytes = Stream.ReadArrayPrependLengthByte();

            Opcode = Stream.ReadUInt16();

            var checkHeaderLength = HeaderLength - (uint) (sizeof(uint) + SenderBytes.Length + ReceiverBytes.Length);
            if (checkHeaderLength > 0)
                HeaderData = new ConanStream(Stream.ReadArray(checkHeaderLength - sizeof(byte) * 2));

            var dataLength = Length - (HeaderLength + sizeof(uint) * 2);
            if (dataLength > 0)
                Data = new ConanStream(Stream.ReadArray(dataLength));

            if (!IsValid)
                Console.WriteLine($"Invalid {GetType().Name}");
        }

        //public uint HeaderLength => HeaderSize + sizeof(uint) * 2; // + length & crc32
        public override bool IsValid => Crc32 == Crypt.Crc32.Calculate(Stream);

        public override string ToString()
        {
            return $"Opcode: {Opcode.ToHex()} Length: {Length}";
        }
    }
}