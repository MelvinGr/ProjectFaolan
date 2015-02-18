using System;
using LibFaolan;
using LibFaolan.Network;
using LibFaolan.Network.Shared;

namespace HexStreamToPacket
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            LibFaolan.DllImport.Console.EnableQuickEditMode();

            while (true)
            {
                Console.Write("Input: ");
                var hexStream = Console.ReadLine();
                if (string.IsNullOrEmpty(hexStream))
                {
                    Error();
                    continue;
                }

                var bytes = Functions.HexStreamToByteArray(hexStream);
                if (bytes == null)
                {
                    Error();
                    continue;
                }

                try
                {
                    var conanPacket = new ConanPacket(new PacketBuffer(bytes));
                    Console.WriteLine("=====================================================================\r\n");
                    Console.WriteLine(PacketUtills.PacketToCsCode(conanPacket));
                    Console.WriteLine("=====================================================================\r\n");
                }
                catch
                {
                    Error();
                }
            }
        }

        private static void Error()
        {
            Console.WriteLine("Error!");
        }
    }
}