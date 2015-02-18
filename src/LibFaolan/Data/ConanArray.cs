using System;

namespace LibFaolan.Data
{
    public class ConanArray
    {
        public ConanArray(params byte[] data)
        {
            if (data.Length > byte.MaxValue)
                throw new Exception("data.Length > byte.MaxValue");

            Data = data;
        }

        public byte Length => (byte) Data.Length;
        public byte[] Data { get; }
        public byte TotalLength => (byte) (Length + sizeof (byte));

        public byte this[int i]
        {
            get { return Data[i]; }
            set { Data[i] = value; }
        }

        public static implicit operator ConanArray(byte[] data)
        {
            return new ConanArray(data);
        }
    }
}