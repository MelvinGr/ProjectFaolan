using System;

namespace LibFaolan.Math
{
    public sealed class Vector3
    {
        public Vector3(UInt32 x, UInt32 y, UInt32 z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public UInt32 X { get; set; }
        public UInt32 Y { get; set; }
        public UInt32 Z { get; set; }
    }
}