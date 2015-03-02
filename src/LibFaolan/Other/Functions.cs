using System;
using System.IO;
using System.Linq;
using System.Net;
using LibFaolan.Network;

namespace LibFaolan.Other
{
    public static class Functions
    {
        private static readonly DateTime DateTime1970 = new DateTime(1970, 1, 1);
        // https://stackoverflow.com/questions/321370/how-can-i-convert-a-hex-string-to-a-byte-array
        public static byte[] HexStreamToByteArray(string hex)
        {
            try
            {
                return Enumerable.Range(0, hex.Length)
                    .Where(x => x%2 == 0)
                    .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                    .ToArray();
            }
            catch
            {
                return null;
            }
        }

        public static void TrimStream(ref MemoryStream stream)
        {
            if (stream.Position == stream.Length)
            {
                stream = new MemoryStream();
                return;
            }

            var remaining = new byte[stream.Length - stream.Position];
            stream.Read(remaining, 0, remaining.Length);
            stream = new MemoryStream(remaining);
        }

        public static void TrimStream(ref ConanStream stream)
        {
            if (stream.Position == stream.Length)
            {
                stream = new ConanStream();
                return;
            }

            var remaining = new byte[stream.Length - stream.Position];
            stream.Read(remaining, 0, remaining.Length);
            stream = new ConanStream(remaining);
        }

        public static ulong SecondsSindsEpoch()
        {
            return (ulong) (DateTime.UtcNow - DateTime1970).TotalSeconds;
        }

        public static void ExitLoop()
        {
            while (true)
            {
                if (Console.ReadKey(false).Key == ConsoleKey.Escape)
                    break;
            }
        }

        public static UInt32 IpStringToUInt32(string ipaddress)
        {
            return BitConverter.ToUInt32(IPAddress.Parse(ipaddress).GetAddressBytes(), 0);
        }
    }
}