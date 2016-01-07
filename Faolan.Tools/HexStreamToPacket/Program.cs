using System;
using Faolan.Core;
using Faolan.Core.Network;

namespace Faolan.Tools.HexStreamToPacket
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            ConsoleTools.EnableQuickEditMode();

            while (true)
            {
                Console.Write("Input: ");
                var hexStream = Console.ReadLine();
                if (string.IsNullOrEmpty(hexStream))
                {
                    Error("string.IsNullOrEmpty(hexStream)");
                    continue;
                }

                var bytes = Functions.HexStreamToByteArray(hexStream);
                if (bytes == null)
                {
                    Error("bytes == null");
                    continue;
                }

                try
                {
                    var conanPacket = new ConanPacket(new ConanStream(bytes));
                    Console.WriteLine("=====================================================================\r\n");
                    Console.WriteLine(PacketUtills.PacketToCsCode(conanPacket));
                    Console.WriteLine("=====================================================================\r\n");
                }
                catch (Exception e)
                {
                    Error(e.Message);
                }
            }
        }

        private static void Error(string txt)
        {
            Console.WriteLine("Error: " + txt);
        }
    }
}