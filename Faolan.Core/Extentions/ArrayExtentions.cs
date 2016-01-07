using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Extentions
{
    public static class ArrayExtentions
    {
        public static byte[] Reverse(this byte[] data)
        {
            Array.Reverse(data);
            return data;
        }

        public static string ToHex(this IEnumerable<byte> value)
        {
            return "{ " + string.Join(", ", value.Select(e => e.ToHex())) + " }";
        }
    }
}