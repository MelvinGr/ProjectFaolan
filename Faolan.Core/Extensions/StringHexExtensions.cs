using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Extensions
{
    public static class StringHexExtensions
    {
        // https://stackoverflow.com/questions/321370/how-can-i-convert-a-hex-string-to-a-byte-array
        public static byte[] HexToByteArray(this string hex)
        {
            try
            {
                return Enumerable.Range(0, hex.Length)
                    .Where(x => x % 2 == 0)
                    .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                    .ToArray();
            }
            catch
            {
                return null;
            }
        }

        public static string ToHex(this IEnumerable<byte> value)
        {
            return "{ " + string.Join(", ", value.Select(e => e.ToHex())) + " }";
        }

        public static string ToHex(this Enum value)
        {
            var enumType = Enum.GetUnderlyingType(value.GetType());

            if (enumType == typeof(byte) || enumType == typeof(sbyte))
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
            return $"0x{value:X8}";
        }

        public static string ToHex(this double value)
        {
            return $"0x{value:X16}";
        }
    }
}