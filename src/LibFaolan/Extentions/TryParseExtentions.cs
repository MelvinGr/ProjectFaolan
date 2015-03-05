using System;
using System.Globalization;

namespace LibFaolan.Extentions
{
    public static class TryParseExtentions
    {
        public static bool TryParseNormalOrHex(this string input, out byte output)
        {
            if (input.StartsWith("0x"))
            {
                return byte.TryParse(input.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out output);
            }

            return byte.TryParse(input, out output);
        }

        public static bool TryParseNormalOrHex(this string input, out UInt16 output)
        {
            if (input.StartsWith("0x"))
            {
                return UInt16.TryParse(input.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out output);
            }

            return UInt16.TryParse(input, out output);
        }

        public static bool TryParseNormalOrHex(this string input, out UInt32 output)
        {
            if (input.StartsWith("0x"))
            {
                return UInt32.TryParse(input.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out output);
            }

            return UInt32.TryParse(input, out output);
        }

        public static bool TryParseNormalOrHex(this string input, out UInt64 output)
        {
            if (input.StartsWith("0x"))
            {
                return UInt64.TryParse(input.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out output);
            }

            return UInt64.TryParse(input, out output);
        }
    }
}