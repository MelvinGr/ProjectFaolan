using System;

namespace LibFaolan.Extentions
{
    public static class ToHexExtensions
    {
        /*public static string ToHex<T>(this T value) where T : struct
        {
            return string.Format("0x{0:X" + Marshal.SizeOf(value) + "}", value);
        }*/

        public static string ToHex(this byte value)
        {
            return string.Format("0x{0:X2}", value);
        }

        public static string ToHex(this sbyte value)
        {
            return string.Format("0x{0:X2}", value);
        }

        public static string ToHex(this UInt16 value)
        {
            return string.Format("0x{0:X4}", value);
        }

        public static string ToHex(this Int16 value)
        {
            return string.Format("0x{0:X4}", value);
        }

        public static string ToHex(this UInt32 value)
        {
            return string.Format("0x{0:X8}", value);
        }

        public static string ToHex(this Int32 value)
        {
            return string.Format("0x{0:X8}", value);
        }

        public static string ToHex(this UInt64 value)
        {
            return string.Format("0x{0:X16}", value);
        }

        public static string ToHex(this Int64 value)
        {
            return string.Format("0x{0:X16}", value);
        }
    }
}