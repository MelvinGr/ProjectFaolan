using System;

namespace Faolan.Core.Extentions
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

            if (enumType == typeof(byte))
                return Convert.ToByte(value).ToHex();
            if (enumType == typeof(ushort) || enumType == typeof(short))
                return Convert.ToUInt16(value).ToHex();
            if (enumType == typeof(uint) || enumType == typeof(int))
                return Convert.ToUInt32(value).ToHex();
            if (enumType == typeof(ulong) || enumType == typeof(long))
                return Convert.ToUInt64(value).ToHex();
            if (enumType == typeof(float))
                return Convert.ToUInt32(value).ToHex();
            if (enumType == typeof(double))
                return Convert.ToUInt64(value).ToHex();

            throw new Exception("Unknown enum type!");
        }

        public static string ToHex(this byte value)
        {
            return $"0x{value:X2}";
        }

        public static string ToHex(this sbyte value)
        {
            return $"0x{value:X2}";
        }

        public static string ToHex(this ushort value)
        {
            return $"0x{value:X4}";
        }

        public static string ToHex(this short value)
        {
            return $"0x{value:X4}";
        }

        public static string ToHex(this uint value)
        {
            return $"0x{value:X8}";
        }

        public static string ToHex(this int value)
        {
            return $"0x{value:X8}";
        }

        public static string ToHex(this ulong value)
        {
            return $"0x{value:X16}";
        }

        public static string ToHex(this long value)
        {
            return $"0x{value:X16}";
        }

        public static string ToHex(this float value)
        {
            return $"0x{(uint) value:X8}";
        }

        public static string ToHex(this double value)
        {
            return $"0x{(ulong) value:X16}";
        }
    }
}