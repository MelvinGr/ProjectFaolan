using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Faolan.Core.Network;

namespace Faolan.Core
{
    public static class Functions
    {
        [DllImport("msvcrt.dll", EntryPoint = "_time32", CallingConvention = CallingConvention.Cdecl)]
        private static extern int time(ref int timer);

        public static int Time()
        {
            var epoch = 0;
            time(ref epoch);
            return epoch;
        }

        [DllImport("ws2_32.dll", CharSet = CharSet.Ansi)]
        public static extern uint inet_addr(string cp);

        [DllImport("ws2_32.dll")]
        public static extern uint htonl(uint hostlong);

        // https://stackoverflow.com/questions/321370/how-can-i-convert-a-hex-string-to-a-byte-array
        public static byte[] HexStreamToByteArray(string hex)
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
                stream = new PacketStream();
                return;
            }

            var remaining = new byte[stream.Length - stream.Position];
            stream.Read(remaining, 0, remaining.Length);
            stream = new ConanStream(remaining);
        }

        public static void ExitLoop(ConsoleKey key = ConsoleKey.Escape)
        {
            while (true)
                if (Console.ReadKey(false).Key == key)
                    break;
        }
    }
}