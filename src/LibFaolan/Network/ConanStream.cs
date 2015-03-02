using System;
using System.IO;
using System.Text;
using LibFaolan.Crypt;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Math;

namespace LibFaolan.Network
{
    public class ConanStream : MemoryStream
    {
        public ConanStream()
        {
        }

        public ConanStream(byte[] data) : base(data)
        {
        }

        public new UInt32 Length => (UInt32) base.Length;

        public new UInt32 Position
        {
            get { return (UInt32) base.Position; }
            set { base.Position = value; }
        }

        public new byte ReadByte()
        {
            return (byte) base.ReadByte();
        }

        public UInt16 ReadUInt16()
        {
            var value = new byte[sizeof (UInt16)];
            Read(value, 0, sizeof (UInt16));
            return BitConverter.ToUInt16(value.Reverse(), 0);
        }

        public UInt32 ReadUInt32()
        {
            var value = new byte[sizeof (UInt32)];
            Read(value, 0, sizeof (UInt32));
            return BitConverter.ToUInt32(value.Reverse(), 0);
        }

        public UInt64 ReadUInt64()
        {
            var value = new byte[sizeof (UInt64)];
            Read(value, 0, sizeof (UInt64));
            return BitConverter.ToUInt64(value.Reverse(), 0);
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            var bytes = ReadArray(length);
            return Encoding.UTF8.GetString(bytes);
        }

        public string ReadShortString()
        {
            var length = ReadByte();
            var bytes = ReadArray(length);
            return Encoding.UTF8.GetString(bytes);
        }

        public byte[] ReadArray(UInt32 length)
        {
            var headerBytes = new byte[length];
            Read(headerBytes, 0, (int) length);
            return headerBytes;
        }

        public ConanArray ReadConanArray()
        {
            var length = ReadByte();
            var bytes = ReadArray(length);
            return new ConanArray(bytes);
        }

        public Vector3 ReadVector3()
        {
            return new Vector3(ReadUInt32(), ReadUInt32(), ReadUInt32());
        }

        ////////////////////////////////////////////////////////

        public new ConanStream WriteByte(byte value)
        {
            base.WriteByte(value);
            return this;
        }

        public ConanStream WriteUInt16(Int16 value)
        {
            WriteUInt16((UInt16) value);
            return this;
        }

        public ConanStream WriteUInt16(UInt16 value)
        {
            var buffer = BitConverter.GetBytes(value).Reverse();
            Write(buffer, 0, sizeof (UInt16));
            return this;
        }

        public ConanStream WriteUInt32(Int32 value)
        {
            WriteUInt32((UInt32) value);
            return this;
        }

        public ConanStream WriteUInt32(UInt32 value)
        {
            var buffer = BitConverter.GetBytes(value).Reverse();
            Write(buffer, 0, sizeof (UInt32));
            return this;
        }

        public ConanStream WriteUInt64(Int64 value)
        {
            WriteUInt64((UInt64) value);
            return this;
        }

        public ConanStream WriteUInt64(UInt64 value)
        {
            var buffer = BitConverter.GetBytes(value).Reverse();
            Write(buffer, 0, sizeof (UInt64));
            return this;
        }

        public ConanStream WriteString(string value)
        {
            WriteUInt16((UInt16) value.Length);
            WriteArray(Encoding.UTF8.GetBytes(value));
            return this;
        }

        public ConanStream WriteShortString(string value)
        {
            if (value.Length > byte.MaxValue)
                throw new Exception("value.Length > byte.MaxValue");

            WriteByte((byte) value.Length);
            WriteArray(Encoding.UTF8.GetBytes(value));
            return this;
        }

        public ConanStream WriteArray(params byte[] value)
        {
            Write(value, 0, value.Length);
            return this;
        }

        public ConanStream WriteConanArray(ConanArray value)
        {
            WriteByte(value.Length);
            WriteArray(value.Data);
            return this;
        }

        public ConanStream WriteVector3(Vector3 value)
        {
            WriteUInt32(value.X);
            WriteUInt32(value.Y);
            WriteUInt32(value.Z);

            return this;
        }

        ////////////////////////////////////////////////////////

        public ConanStream WriteHeader<T>(ConanArray sender, ConanArray receiver, byte[] headerData, T opcode,
            bool minOpcode) where T : struct
        {
            WriteUInt32(0); // Write empty length
            WriteUInt32(0); // write empty crc32

            //Write Headerlength
            WriteUInt32(sender.TotalLength + receiver.TotalLength + (headerData?.Length ?? 0)
                        + sizeof (byte)*2 + (minOpcode ? sizeof (UInt16) : sizeof (UInt32)));

            //Write sender
            WriteByte(0x0a);
            WriteConanArray(sender);

            //Write receiver
            WriteByte(0x12);
            WriteConanArray(receiver);

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

        public ConanStream WriteLengthHash()
        {
            var oldPos = Position;
            Position = 0;
            WriteUInt32(Length - sizeof (UInt32));

            var hash = Crc32.CalculateForPacketBuffer(this);
            Position = sizeof (UInt32);
            WriteUInt32(hash);
            Position = oldPos;
            return this;
        }

        public void Send(NetworkClient client)
        {
            WriteLengthHash();
            client.Send(ToArray());
        }
    }
}