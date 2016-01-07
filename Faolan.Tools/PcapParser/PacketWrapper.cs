using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using Faolan.Common.Network;
using PacketDotNet;
using SharpPcap;

namespace Faolan.Tools.PcapParser
{
    internal class PacketWrapper
    {
        // 262144 = max packet length, 300 = random for a bit of space (headers etc..)
        private const uint MaxPacketLen = 262144 - 300;
        private static readonly PhysicalAddress NullAddress = new PhysicalAddress(new byte[] {0, 0, 0, 0, 0, 0});
        public ConanPacket ConanPacket;
        public uint Id;

        public PacketWrapper(uint id, IPv4Packet ipv4, ushort sourcePort, ushort destPort, ConanPacket conanPacket)
        {
            Id = id;
            ConanPacket = conanPacket;

            EthernetPacket = new EthernetPacket(NullAddress, NullAddress, EthernetPacketType.IpV4)
            {
                PayloadPacket = ipv4
            };
            Ipv4Packet.PayloadPacket = new UdpPacket(sourcePort, destPort); // conandata
        }

        public EthernetPacket EthernetPacket { get; set; }
        public IPv4Packet Ipv4Packet => (IPv4Packet) EthernetPacket.PayloadPacket;
        public UdpPacket UdpPacket => (UdpPacket) Ipv4Packet.PayloadPacket;

        public IEnumerable<RawCapture> GetWriteableCaptures()
        {
            var conanPayload = ConanPacket.ToArray();
            if (conanPayload.Length > MaxPacketLen)
            {
                Console.WriteLine("Have to split packet, (ignore the incorrect sequence check)");
                for (uint i = 0; i < conanPayload.Length; i += MaxPacketLen)
                {
                    UdpPacket.PayloadData = conanPayload.Skip((int) i).Take((int) MaxPacketLen).ToArray();
                    yield return
                        new RawCapture(LinkLayers.Ethernet, new PosixTimeval(Id, i / 1000), EthernetPacket.Bytes)
                        ;
                }
            }
            else
            {
                UdpPacket.PayloadData = conanPayload;
                yield return new RawCapture(LinkLayers.Ethernet, new PosixTimeval(Id, 0), EthernetPacket.Bytes);
            }
        }
    }
}