using System;
using System.Collections.Generic;
using LibFaolan.Network;

namespace PcapParser
{
    internal class TestClass
    {
        private readonly string _client;
        private readonly string _server;
        private Dictionary<UInt32, UInt32> _receivedOpcodes = new Dictionary<uint, uint>();

        public TestClass(string server, string client)
        {
            _server = server;
            _client = client;
        }

        public void Parse(List<PacketWrapper> parsedPackets)
        {
            var startReadingServer = false;
            ConanPacket clientPacket = null;
            List<ConanPacket> followingPackets = null;

            foreach (var packet in parsedPackets)
            {
                var packetSourceAddress = packet.Ipv4Packet.SourceAddress.ToString();
                if (packetSourceAddress == _server && startReadingServer)
                {
                    followingPackets.Add(packet.ConanPacket);
                }
                else if (packetSourceAddress == _client)
                {
                    if (startReadingServer)
                    {
                        startReadingServer = false;
                        Handle(clientPacket, followingPackets);
                        clientPacket = null;
                        followingPackets = null;
                    }
                    else
                    {
                        startReadingServer = true;
                        clientPacket = packet.ConanPacket;
                        followingPackets = new List<ConanPacket>();
                    }
                }
            }
        }

        private void Handle(ConanPacket conanPacket, List<ConanPacket> followingPackets)
        {
            Console.WriteLine();
        }
    }
}