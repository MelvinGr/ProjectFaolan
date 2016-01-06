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

        public static bool TryParseNormalOrHex(this string input, out ushort output)
        {
            if (input.StartsWith("0x"))
            {
                return ushort.TryParse(input.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out output);
            }

            return ushort.TryParse(input, out output);
        }

        public static bool TryParseNormalOrHex(this string input, out uint output)
        {
            if (input.StartsWith("0x"))
            {
                return uint.TryParse(input.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out output);
            }

            return uint.TryParse(input, out output);
        }

        public static bool TryParseNormalOrHex(this string input, out ulong output)
        {
            if (input.StartsWith("0x"))
            {
                return ulong.TryParse(input.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out output);
            }

            return ulong.TryParse(input, out output);
        }
    }
}