using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using LibFaolan.Network;

namespace LibFaolan.Other
{
    public static class Functions
    {
        // http://blog.rastating.com/setting-default-currentculture-in-all-versions-of-net/
        public static void SetDefaultCulture(CultureInfo culture)
        {
            var type = typeof (CultureInfo);

            try
            {
                type.InvokeMember("s_userDefaultCulture",
                    BindingFlags.SetField | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    culture,
                    new object[] {culture});

                type.InvokeMember("s_userDefaultUICulture",
                    BindingFlags.SetField | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    culture,
                    new object[] {culture});
            }
            catch
            {
            }

            try
            {
                type.InvokeMember("m_userDefaultCulture",
                    BindingFlags.SetField | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    culture,
                    new object[] {culture});

                type.InvokeMember("m_userDefaultUICulture",
                    BindingFlags.SetField | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    culture,
                    new object[] {culture});
            }
            catch
            {
            }
        }

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
                stream = new PacketStream();
                return;
            }

            var remaining = new byte[stream.Length - stream.Position];
            stream.Read(remaining, 0, remaining.Length);
            stream = new ConanStream(remaining);
        }

        public static void ExitLoop()
        {
            while (true)
            {
                if (Console.ReadKey(false).Key == ConsoleKey.Escape)
                    break;
            }
        }
    }
}