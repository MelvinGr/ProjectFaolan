using System;
using Faolan.Core.Crypt;

namespace Faolan.Core.Network
{
    public sealed class PacketStream : ConanStream
    {
        public PacketStream()
        {
        }

        public PacketStream(byte[] data) : base(data)
        {
        }

        public PacketStream WriteHeader<T>(byte[] sender, byte[] receiver, byte[] headerData, T opcode,
            bool minOpcode = true) where T : struct
        {
            WriteUInt32(0); // Write empty length
            WriteUInt32(0); // write empty crc32

            //Write Headerlength
            WriteUInt32(1 + sender.Length + 1 + receiver.Length + (headerData?.Length ?? 0)
                        + sizeof(byte) * 2 + (minOpcode ? sizeof(ushort) : sizeof(uint)));

            //Write sender
            WriteByte(0x0a);
            WriteArrayPrependLengthByte(sender);

            //Write receiver
            WriteByte(0x12);
            WriteArrayPrependLengthByte(receiver);

            //write opcode
            if (minOpcode) // hmmmm should be done better... (opcode can be given as uint* or as a enum)
                WriteUInt16(Convert.ToUInt16(opcode));
            else
                WriteUInt32(Convert.ToUInt32(opcode));

            //Write headerdata
            if (headerData?.Length > 0)
                WriteArray(headerData);

            return this;
        }

        private void WriteLengthHash()
        {
            var oldPos = Position;
            Position = 0;
            WriteUInt32(Length - sizeof(uint));

            var hash = Crc32.CalculateForPacketBuffer(this);
            Position = sizeof(uint);
            WriteUInt32(hash);
            Position = oldPos;
        }

        public override void Send(INetworkClient client)
        {
            WriteLengthHash();
            base.Send(client);
        }
    }
}