using System;
using System.IO;
using System.Text;
using LibFaolan.Crypt;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network.Internal;

namespace LibFaolan.Network.Shared
{
    public class PacketBuffer
    {
        private readonly MemoryStream _stream;

        public PacketBuffer()
        {
            _stream = new MemoryStream();
        }

        public PacketBuffer(byte[] data)
        {
            _stream = new MemoryStream(data);
        }

        public PacketBuffer(MemoryStream data)
        {
            _stream = data;
        }

        public UInt32 Length => (UInt32) _stream.Length;

        public UInt32 Position
        {
            get { return (UInt32) _stream.Position; }
            set { _stream.Position = value; }
        }

        public byte ReadByte()
        {
            return (byte) _stream.ReadByte();
        }

        public UInt16 ReadUInt16()
        {
            var value = new byte[sizeof (UInt16)];
            _stream.Read(value, 0, sizeof (UInt16));
            return BitConverter.ToUInt16(value.Reverse(), 0);
        }

        public UInt32 ReadUInt32()
        {
            var value = new byte[sizeof (UInt32)];
            _stream.Read(value, 0, sizeof (UInt32));
            return BitConverter.ToUInt32(value.Reverse(), 0);
        }

        public UInt64 ReadUInt64()
        {
            var value = new byte[sizeof (UInt64)];
            _stream.Read(value, 0, sizeof (UInt64));
            return BitConverter.ToUInt64(value.Reverse(), 0);
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            var bytes = ReadArray(length);
            return Encoding.UTF8.GetString(bytes);
        }

        public byte[] ReadArray(UInt32 length)
        {
            var headerBytes = new byte[length];
            _stream.Read(headerBytes, 0, (int) length);
            return headerBytes;
        }

        public ConanArray ReadConanArray()
        {
            var length = ReadByte();
            var bytes = ReadArray(length);
            return new ConanArray(bytes);
        }

        ////////////////////////////////////////////////////////

        public PacketBuffer WriteByte(byte value)
        {
            _stream.WriteByte(value);
            return this;
        }

        public PacketBuffer WriteUInt16(Int16 value)
        {
            WriteUInt16((UInt16) value);
            return this;
        }

        public PacketBuffer WriteUInt16(UInt16 value)
        {
            var buffer = BitConverter.GetBytes(value).Reverse();
            _stream.Write(buffer, 0, sizeof (UInt16));
            return this;
        }

        public PacketBuffer WriteUInt32(Int32 value)
        {
            WriteUInt32((UInt32) value);
            return this;
        }

        public PacketBuffer WriteUInt32(UInt32 value)
        {
            var buffer = BitConverter.GetBytes(value).Reverse();
            _stream.Write(buffer, 0, sizeof (UInt32));
            return this;
        }

        public PacketBuffer WriteUInt64(Int64 value)
        {
            WriteUInt64((UInt64) value);
            return this;
        }

        public PacketBuffer WriteUInt64(UInt64 value)
        {
            var buffer = BitConverter.GetBytes(value).Reverse();
            _stream.Write(buffer, 0, sizeof (UInt64));
            return this;
        }

        public PacketBuffer WriteString(string value)
        {
            WriteUInt16((UInt16) value.Length);
            WriteArray(Encoding.UTF8.GetBytes(value));
            return this;
        }

        public PacketBuffer WriteArray(params byte[] value)
        {
            _stream.Write(value, 0, value.Length);
            return this;
        }

        public PacketBuffer WriteConanArray(ConanArray value)
        {
            WriteByte(value.Length);
            WriteArray(value.Data);
            return this;
        }

        ////////////////////////////////////////////////////////

        public PacketBuffer WriteHeader<T>(ConanArray sender, ConanArray receiver, byte[] headerData, T opcode,
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
            if (minOpcode) // hmmmm... (opcode can be given as uint* or as a enum)
                WriteUInt16(Convert.ToUInt16(opcode));
            else
                WriteUInt32(Convert.ToUInt32(opcode));

            //Write headerdata
            if (headerData?.Length > 0)
                WriteArray(headerData);

            return this;
        }

        public PacketBuffer WriteInternalHeader(byte sender, byte receiver, InternalOpcodes opcode)
        {
            WriteUInt32(0); // Write empty length
            WriteUInt32(0); // write empty crc32

            WriteByte(sender);
            WriteByte(receiver);
            WriteUInt16((UInt16) opcode);

            return this;
        }

        public PacketBuffer WriteLengthHash()
        {
            var oldPos = _stream.Position;
            _stream.Position = 0;
            WriteUInt32(Length - sizeof (UInt32));

            var hash = Crc32.CalculateForPacketBuffer(this);
            _stream.Position = sizeof (UInt32);
            WriteUInt32(hash);
            _stream.Position = oldPos;
            return this;
        }

        public void Send(NetworkClient client, bool compressed = false)
        {
            WriteLengthHash();

            /*if (!compressed)
            {
                client.Send(new Packet(this));
                return;
            }*/
            client.Send(new Packet(this));
        }

        public void SendInternal(NetworkClient client)
        {
            WriteLengthHash();
            client.Send(new Packet(this));
        }

        public void SendInternal(InternalConnection client)
        {
            WriteLengthHash();
            client.Send(new Packet(this));
        }

        public byte[] GetBuffer()
        {
            return _stream.GetBuffer();
        }

        public byte[] ToArray()
        {
            return _stream.ToArray();
        }
    }
}