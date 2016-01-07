using System;
using System.IO;
using System.Text;
using Faolan.Core.Extentions;
using Faolan.Core.Math;

namespace Faolan.Core.Network
{
    public class ConanStream : MemoryStream
    {
        public ConanStream()
        {
        }

        public ConanStream(byte[] data) : base(data)
        {
        }

        public new uint Length => (uint) base.Length;

        public new uint Position
        {
            get => (uint) base.Position;
            set => base.Position = value;
        }

        public new byte ReadByte()
        {
            return (byte) base.ReadByte();
        }

        public ushort ReadUInt16()
        {
            return BitConverter.ToUInt16(ReadArray(sizeof(ushort)).Reverse(), 0);
        }

        public uint ReadUInt32()
        {
            return BitConverter.ToUInt32(ReadArray(sizeof(uint)).Reverse(), 0);
        }

        public ulong ReadUInt64()
        {
            return BitConverter.ToUInt64(ReadArray(sizeof(ulong)).Reverse(), 0);
        }

        public float ReadFloat()
        {
            return BitConverter.ToSingle(ReadArray(sizeof(float)).Reverse(), 0);
        }

        public string ReadShortString()
        {
            return Encoding.UTF8.GetString(ReadArrayPrependLengthByte());
        }

        public string ReadString()
        {
            return Encoding.UTF8.GetString(ReadArrayPrependLengthUInt16());
        }

        public Vector3 ReadVector3()
        {
            return new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        }

        public byte[] ReadArrayPrependLengthByte()
        {
            return ReadArray(ReadByte());
        }

        public byte[] ReadArrayPrependLengthUInt16()
        {
            return ReadArray(ReadUInt16());
        }

        public byte[] ReadArrayPrependLengthUInt32()
        {
            return ReadArray(ReadUInt32());
        }

        public byte[] ReadArray(uint length)
        {
            var buffer = new byte[length];
            Read(buffer, 0, (int) length);
            return buffer;
        }

        public new ConanStream WriteByte(byte value)
        {
            base.WriteByte(value);
            return this;
        }

        public ConanStream WriteUInt16(Enum value)
        {
            return WriteUInt16(Convert.ToUInt16(value));
        }

        public ConanStream WriteUInt16(short value)
        {
            return WriteUInt16((ushort) value);
        }

        public ConanStream WriteUInt16(ushort value)
        {
            return WriteArray(BitConverter.GetBytes(value).Reverse());
        }

        public ConanStream WriteUInt32(Enum value)
        {
            return WriteUInt32(Convert.ToUInt32(value));
        }

        public ConanStream WriteUInt32(int value)
        {
            return WriteUInt32((uint) value);
        }

        public ConanStream WriteUInt32(uint value)
        {
            return WriteArray(BitConverter.GetBytes(value).Reverse());
        }

        public ConanStream WriteUInt64(Enum value)
        {
            return WriteUInt64(Convert.ToUInt64(value));
        }

        public ConanStream WriteUInt64(long value)
        {
            return WriteUInt64((ulong) value);
        }

        public ConanStream WriteUInt64(ulong value)
        {
            return WriteArray(BitConverter.GetBytes(value).Reverse());
        }

        public ConanStream WriteFloat(float value)
        {
            return WriteArray(BitConverter.GetBytes(value));
        }

        public ConanStream WriteShortString(string value)
        {
            return WriteArrayPrependLengthByte(Encoding.UTF8.GetBytes(value));
        }

        public ConanStream WriteString(string value)
        {
            return WriteArrayPrependLengthUInt16(Encoding.UTF8.GetBytes(value));
        }

        public ConanStream WriteArrayPrependLengthByte(ConanStream stream)
        {
            return WriteArrayPrependLengthByte(stream.ToArray());
        }

        public ConanStream WriteArrayPrependLengthUInt16(ConanStream stream, bool removeLen = false)
        {
            return WriteArrayPrependLengthUInt16(stream.ToArray(), removeLen);
        }

        public ConanStream WriteArrayPrependLengthUInt32(ConanStream stream)
        {
            return WriteArrayPrependLengthUInt32(stream.ToArray());
        }

        public ConanStream WriteArray(params byte[] value)
        {
            Write(value, 0, value.Length);
            return this;
        }

        public ConanStream WriteVector3(Vector3 value)
        {
            WriteFloat(value.X);
            WriteFloat(value.Y);
            return WriteFloat(value.Z);
        }

        public ConanStream WriteArrayPrependLengthByte(byte[] value)
        {
            if (value.Length > byte.MaxValue)
                throw new Exception("value.Length > byte.MaxValue");

            WriteByte((byte) value.Length);
            return WriteArray(value);
        }

        public ConanStream WriteArrayPrependLengthUInt16(byte[] value, bool removeLen = false)
        {
            if (value.Length > ushort.MaxValue)
                throw new Exception("value.Length > UInt16.MaxValue");

            if (removeLen)
                WriteUInt16((ushort) (value.Length - sizeof(ushort)));
            else
                WriteUInt16((ushort) value.Length);

            return WriteArray(value);
        }

        public ConanStream WriteArrayPrependLengthUInt32(byte[] value)
        {
            WriteUInt32(value.Length);
            return WriteArray(value);
        }

        public virtual void Send(INetworkClient client)
        {
            client.Send(ToArray());
        }
    }
}