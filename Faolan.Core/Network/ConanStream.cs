using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Faolan.Core.Data;

namespace Faolan.Core.Network
{
    public class ConanStream : MemoryStream
    {
        public ConanStream()
        {
        }

        public ConanStream(params byte[] data) : base(data)
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
            return BitConverter.ToUInt16(ReadArray(sizeof(ushort)).Reverse().ToArray(), 0);
        }

        public T ReadUInt16<T>() where T : Enum
        {
            return (T) Enum.ToObject(typeof(T), ReadUInt16());
        }

        public uint ReadUInt32()
        {
            return BitConverter.ToUInt32(ReadArray(sizeof(uint)).Reverse().ToArray(), 0);
        }

        public T ReadUInt32<T>() where T : Enum
        {
            return (T) Enum.ToObject(typeof(T), ReadUInt32());
        }

        public ulong ReadUInt64()
        {
            return BitConverter.ToUInt64(ReadArray(sizeof(ulong)).Reverse().ToArray(), 0);
        }

        public float ReadFloat()
        {
            return BitConverter.ToSingle(ReadArray(sizeof(float)).Reverse().ToArray(), 0);
        }

        public string ReadStringByteLength()
        {
            return Encoding.UTF8.GetString(ReadArrayByteLength());
        }

        public string ReadString()
        {
            return Encoding.UTF8.GetString(ReadArrayUInt16Length());
        }

        public Vector3 ReadVector3()
        {
            return new(ReadFloat(), ReadFloat(), ReadFloat());
        }

        public byte[] ReadArrayByteLength()
        {
            return ReadArray(ReadByte());
        }

        public byte[] ReadArrayUInt16Length()
        {
            return ReadArray(ReadUInt16());
        }

        public byte[] ReadArrayUInt32Length()
        {
            return ReadArray(ReadUInt32());
        }

        public byte[] ReadArray(uint length)
        {
            if (length > int.MaxValue)
                throw new Exception("length > Int32.MaxValue");

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
            return WriteArray(BitConverter.GetBytes(value).Reverse().ToArray());
        }

        public ConanStream WriteUInt32(Enum value)
        {
            return WriteUInt32(Convert.ToUInt32(value));
        }

        public ConanStream WriteUInt32(int value)
        {
            return WriteUInt32((uint) value);
        }

        public ConanStream WriteUInt32(long value)
        {
            return WriteUInt32((uint) value);
        }

        public ConanStream WriteUInt32(uint value)
        {
            return WriteArray(BitConverter.GetBytes(value).Reverse().ToArray());
        }

        public ConanStream WriteUInt32(bool value)
        {
            return WriteUInt32(value ? 1 : 0);
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
            return WriteArray(BitConverter.GetBytes(value).Reverse().ToArray());
        }

        public ConanStream WriteFloat(float value)
        {
            return WriteArray(BitConverter.GetBytes(value));
        }

        public ConanStream WriteStringByteLength(string value)
        {
            return WriteArrayByteLength(Encoding.UTF8.GetBytes(value ?? ""));
        }

        public ConanStream WriteString(string value)
        {
            return WriteArrayUInt16Length(Encoding.UTF8.GetBytes(value ?? ""));
        }

        public ConanStream WriteArrayByteLength(ConanStream stream)
        {
            return WriteArrayByteLength(stream.ToArray());
        }

        public ConanStream WriteArrayUInt16Length(ConanStream stream)
        {
            return WriteArrayUInt16Length(stream.ToArray());
        }

        public ConanStream WriteArrayUInt32Length(ConanStream stream)
        {
            return WriteArrayUInt32Length(stream.ToArray());
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

        public ConanStream WritePosition(Character character)
        {
            WriteFloat(character.PositionX);
            WriteFloat(character.PositionY);
            return WriteFloat(character.PositionZ);
        }

        public ConanStream WriteRotation(Character character)
        {
            WriteFloat(character.RotationX);
            WriteFloat(character.RotationY);
            return WriteFloat(character.RotationZ);
        }

        public ConanStream WritePosition(MapObject worldObject)
        {
            WriteFloat(worldObject.PositionX);
            WriteFloat(worldObject.PositionY);
            return WriteFloat(worldObject.PositionZ);
        }

        public ConanStream WriteRotation(MapObject worldObject)
        {
            WriteFloat(worldObject.RotationX);
            WriteFloat(worldObject.RotationY);
            return WriteFloat(worldObject.RotationZ);
        }

        public ConanStream WriteArrayByteLength(params byte[] value)
        {
            if (value.Length > byte.MaxValue)
                throw new Exception("value.Length > byte.MaxValue");

            WriteByte((byte) value.Length);
            return WriteArray(value);
        }

        public ConanStream WriteArrayUInt16Length(params byte[] value)
        {
            if (value.Length > ushort.MaxValue)
                throw new Exception("value.Length > UInt16.MaxValue");

            WriteUInt16((ushort) value.Length);
            return WriteArray(value);
        }

        public ConanStream WriteArrayUInt32Length(params byte[] value)
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