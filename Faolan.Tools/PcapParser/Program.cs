using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpPcap.LibPcap;

namespace Faolan.Tools.PcapParser
{
    public class Program
    {
        private static void WritePacketWrappers(string path, List<PacketWrapper> parsedPackets)
        {
            var writer = new CaptureFileWriterDevice(path, FileMode.CreateNew);
            writer.Open();

            foreach (var p in parsedPackets.SelectMany(pw => pw.GetWriteableCaptures()))
                writer.Write(p);

            writer.Close();
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Common.DllImport.Console.EnableQuickEditMode();

            var parsedPackets = new PcapParser(@"..\..\..\dumps\1.pcap", "37.18.193.23", "192.168.0.105").Parse();
            //WritePacketWrappers(@"..\..\..\dumps\1_decompressed.pcap", parsedPackets);

            var testClass = new TestClass("37.18.193.23", "192.168.0.105");
            testClass.Parse(parsedPackets);

            Console.WriteLine();
        }
    }
}