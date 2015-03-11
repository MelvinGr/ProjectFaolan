using System;

namespace LibFaolan.Extentions
{
    public static class ToHexExtensions
    {
        /*public static string ToHex<T>(this T value) where T : struct
        {
            return string.Format("0x{0:X" + Marshal.SizeOf(value) + "}", value);
        }*/

        public static string ToHex(this Enum value)
        {
            var enumType = Enum.GetUnderlyingType(value.GetType());

            if (enumType == typeof (byte)) return Convert.ToByte(value).ToHex();
            if (enumType == typeof (UInt16) || enumType == typeof (Int16)) return Convert.ToUInt16(value).ToHex();
            if (enumType == typeof (UInt32) || enumType == typeof (Int32)) return Convert.ToUInt32(value).ToHex();
            if (enumType == typeof (UInt64) || enumType == typeof (Int64)) return Convert.ToUInt64(value).ToHex();
            if (enumType == typeof (float)) return Convert.ToUInt32(value).ToHex();
            if (enumType == typeof (double)) return Convert.ToUInt64(value).ToHex();

            throw new Exception("Unknown enum type!");
        }

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

        public static string ToHex(this float value)
        {
            return string.Format("0x{0:X8}", (UInt32) value); //?
        }

        public static string ToHex(this double value)
        {
            return string.Format("0x{0:X16}", (UInt64) value); //?
        }
    }
}