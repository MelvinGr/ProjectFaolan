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

        public new UInt32 Length => (UInt32) base.Length;

        public new UInt32 Position
        {
            get { return (UInt32) base.Position; }
            set { base.Position = value; }
        }

        public new byte ReadByte() => (byte) base.ReadByte();
        public UInt16 ReadUInt16() => BitConverter.ToUInt16(ReadArray(sizeof (UInt16)).Reverse(), 0);
        public UInt32 ReadUInt32() => BitConverter.ToUInt32(ReadArray(sizeof (UInt32)).Reverse(), 0);
        public UInt64 ReadUInt64() => BitConverter.ToUInt64(ReadArray(sizeof (UInt64)).Reverse(), 0);
        public float ReadFloat() => BitConverter.ToSingle(ReadArray(sizeof (float)).Reverse(), 0);
        public string ReadShortString() => Encoding.UTF8.GetString(ReadArrayPrependLengthByte());
        public string ReadString() => Encoding.UTF8.GetString(ReadArrayPrependLengthUInt16());
        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        public byte[] ReadArrayPrependLengthByte() => ReadArray(ReadByte());
        public byte[] ReadArrayPrependLengthUInt16() => ReadArray(ReadUInt16());
        public byte[] ReadArrayPrependLengthUInt32() => ReadArray(ReadUInt32());

        public byte[] ReadArray(UInt32 length)
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
        public ConanStream WriteUInt16(Int16 value) => WriteUInt16((UInt16) value);
        public ConanStream WriteUInt16(UInt16 value) => WriteArray(BitConverter.GetBytes(value).Reverse());
        public ConanStream WriteUInt32(Enum value) => WriteUInt32(Convert.ToUInt32(value));
        public ConanStream WriteUInt32(Int32 value) => WriteUInt32((UInt32) value);
        public ConanStream WriteUInt32(UInt32 value) => WriteArray(BitConverter.GetBytes(value).Reverse());
        public ConanStream WriteUInt64(Enum value) => WriteUInt64(Convert.ToUInt64(value));
        public ConanStream WriteUInt64(Int64 value) => WriteUInt64((UInt64) value);
        public ConanStream WriteUInt64(UInt64 value) => WriteArray(BitConverter.GetBytes(value).Reverse());
        public ConanStream WriteFloat(float value) => WriteArray(BitConverter.GetBytes(value).Reverse());
        public ConanStream WriteShortString(string value) => WriteArrayPrependLengthByte(Encoding.UTF8.GetBytes(value));
        public ConanStream WriteString(string value) => WriteArrayPrependLengthUInt16(Encoding.UTF8.GetBytes(value));

        public ConanStream WriteArrayPrependLengthByte(ConanStream stream)
            => WriteArrayPrependLengthByte(stream.ToArray());

        public ConanStream WriteArrayPrependLengthUInt16(ConanStream stream)
            => WriteArrayPrependLengthUInt16(stream.ToArray());

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

        public ConanStream WriteArrayPrependLengthUInt16(byte[] value)
        {
            if (value.Length > UInt16.MaxValue)
                throw new Exception("value.Length > UInt16.MaxValue");

            WriteUInt16((UInt16) value.Length);
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