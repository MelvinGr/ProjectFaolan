using System;
using System.IO;
using System.Text;
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

        public new uint Length => (uint) base.Length;

        public new uint Position
        {
            get { return (uint) base.Position; }
            set { base.Position = value; }
        }

        public new byte ReadByte() => (byte) base.ReadByte();
        public ushort ReadUInt16() => BitConverter.ToUInt16(ReadArray(sizeof (ushort)).Reverse(), 0);
        public uint ReadUInt32() => BitConverter.ToUInt32(ReadArray(sizeof (uint)).Reverse(), 0);
        public ulong ReadUInt64() => BitConverter.ToUInt64(ReadArray(sizeof (ulong)).Reverse(), 0);
        public float ReadFloat() => BitConverter.ToSingle(ReadArray(sizeof (float)).Reverse(), 0);
        public string ReadShortString() => Encoding.UTF8.GetString(ReadArrayPrependLengthByte());
        public string ReadString() => Encoding.UTF8.GetString(ReadArrayPrependLengthUInt16());
        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        public byte[] ReadArrayPrependLengthByte() => ReadArray(ReadByte());
        public byte[] ReadArrayPrependLengthUInt16() => ReadArray(ReadUInt16());
        public byte[] ReadArrayPrependLengthUInt32() => ReadArray(ReadUInt32());

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

        public ConanStream WriteUInt16(Enum value) => WriteUInt16(Convert.ToUInt16(value));
        public ConanStream WriteUInt16(short value) => WriteUInt16((ushort) value);
        public ConanStream WriteUInt16(ushort value) => WriteArray(BitConverter.GetBytes(value).Reverse());
        public ConanStream WriteUInt32(Enum value) => WriteUInt32(Convert.ToUInt32(value));
        public ConanStream WriteUInt32(int value) => WriteUInt32((uint) value);
        public ConanStream WriteUInt32(uint value) => WriteArray(BitConverter.GetBytes(value).Reverse());
        public ConanStream WriteUInt64(Enum value) => WriteUInt64(Convert.ToUInt64(value));
        public ConanStream WriteUInt64(long value) => WriteUInt64((ulong) value);
        public ConanStream WriteUInt64(ulong value) => WriteArray(BitConverter.GetBytes(value).Reverse());
        public ConanStream WriteFloat(float value) => WriteArray(BitConverter.GetBytes(value)); // dont reverse here ???
        public ConanStream WriteShortString(string value) => WriteArrayPrependLengthByte(Encoding.UTF8.GetBytes(value));
        public ConanStream WriteString(string value) => WriteArrayPrependLengthUInt16(Encoding.UTF8.GetBytes(value));

        public ConanStream WriteArrayPrependLengthByte(ConanStream stream)
            => WriteArrayPrependLengthByte(stream.ToArray());

        public ConanStream WriteArrayPrependLengthUInt16(ConanStream stream, bool removeLen = false)
            => WriteArrayPrependLengthUInt16(stream.ToArray(), removeLen);

        public ConanStream WriteArrayPrependLengthUInt32(ConanStream stream)
            => WriteArrayPrependLengthUInt32(stream.ToArray());

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
                WriteUInt16((ushort) (value.Length - sizeof (ushort)));
            else
                WriteUInt16((ushort) value.Length);

            return WriteArray(value);
        }

        public ConanStream WriteArrayPrependLengthUInt32(byte[] value)
        {
            WriteUInt32(value.Length);
            return WriteArray(value);
        }

        public virtual void Send(NetworkClient client)
        {
            client.Send(ToArray());
        }
    }
}