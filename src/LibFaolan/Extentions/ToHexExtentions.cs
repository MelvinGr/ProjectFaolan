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
            if (enumType == typeof (ushort) || enumType == typeof (short)) return Convert.ToUInt16(value).ToHex();
            if (enumType == typeof (uint) || enumType == typeof (int)) return Convert.ToUInt32(value).ToHex();
            if (enumType == typeof (ulong) || enumType == typeof (long)) return Convert.ToUInt64(value).ToHex();
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

        public static string ToHex(this ushort value)
        {
            return string.Format("0x{0:X4}", value);
        }

        public static string ToHex(this short value)
        {
            return string.Format("0x{0:X4}", value);
        }

        public static string ToHex(this uint value)
        {
            return string.Format("0x{0:X8}", value);
        }

        public static string ToHex(this int value)
        {
            return string.Format("0x{0:X8}", value);
        }

        public static string ToHex(this ulong value)
        {
            return string.Format("0x{0:X16}", value);
        }

        public static string ToHex(this long value)
        {
            return string.Format("0x{0:X16}", value);
        }

        public static string ToHex(this float value)
        {
            return string.Format("0x{0:X8}", (uint) value); //?
        }

        public static string ToHex(this double value)
        {
            return string.Format("0x{0:X16}", (ulong) value); //?
        }
    }
}