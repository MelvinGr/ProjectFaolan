using System;
using Faolan.Core.Crypt;

namespace Faolan.Core.Network
{
    public class PacketStream : ConanStream
    {
        public PacketStream()
        {
        }

        public PacketStream(params byte[] data) : base(data)
        {
        }

        public PacketStream WriteHeader<T>(byte[] sender, byte[] receiver, byte[] headerData, T opcode, bool shortOpcode = true) where T : struct
        {
            WriteUInt32(0); // Write empty length
            WriteUInt32(0); // write empty crc32

            //Write Headerlength
            WriteUInt32(sizeof(byte) + sender.Length + 
                        sizeof(byte) + receiver.Length +
                        (shortOpcode ? sizeof(ushort) : sizeof(uint)) +
                        (headerData?.Length ?? 0) +
                        sizeof(byte) * 2);

            //Write sender
            WriteByte(0x0a);
            WriteArrayByteLength(sender);

            //Write receiver
            WriteByte(0x12);
            WriteArrayByteLength(receiver);

            //write opcode
            if (shortOpcode)
                WriteUInt16(Convert.ToUInt16(opcode));
            else
                WriteUInt32(Convert.ToUInt32(opcode));

            //Write headerData
            if (headerData?.Length > 0)
                WriteArray(headerData);

            return this;
        }

        public void WriteLengthHash()
        {
            var oldPos = Position;
            Position = 0;
            WriteUInt32(Length - sizeof(uint));
            WriteUInt32(Crc32.Calculate(this));
            Position = oldPos;
        }

        public override void Send(INetworkClient client)
        {
            WriteLengthHash();
            base.Send(client);
        }
    }
}