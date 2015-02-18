using System;
using System.Collections.Generic;
using LibFaolan;
using LibFaolan.Network;
using LibFaolan.Network.Shared;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using Packet = PacketDotNet.Packet;

namespace PcapParser
{
    internal class PcapParser
    {
        private readonly List<PacketWrapper> _capturedPackets;
        private readonly CaptureFileReaderDevice _device;
        private readonly string _serverIp, _clientIp;
        private readonly WireProtocol<ConanPacket> _serverWireProtocol, _clientWireProtocol;
        private LibFaolanCompression _libFaolanCompression;
        private uint _packetCounter;

        public PcapParser(string pcappath, string serverIp, string clientIp)
        {
            _serverIp = serverIp;
            _clientIp = clientIp;

            _capturedPackets = new List<PacketWrapper>();
            _serverWireProtocol = new WireProtocol<ConanPacket>();
            _clientWireProtocol = new WireProtocol<ConanPacket>();

            _device = new CaptureFileReaderDevice(pcappath)
            {
                //Filter = "(ip and tcp) and (host " + serverIp + " or host " + clientIp + ")"
                Filter = "(ip and tcp) and host " + serverIp
            };
            _device.OnPacketArrival += OnPacketArrival;
        }

        private bool IsZlibStartPacket(byte[] data) => // zlib compression (0x80000005)
            (data[0] == 0x80 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x05);

        private void OnPacketArrival(object sender, CaptureEventArgs e)
        {
            var tcp = (TcpPacket) Packet.ParsePacket(e.Packet.LinkLayerType, e.Packet.Data).Extract(typeof (TcpPacket));
            var ipv4 = (IPv4Packet) tcp.ParentPacket;
            var sourceAddress = ipv4.SourceAddress.ToString();
            var data = tcp.PayloadData;

            if (sourceAddress == _serverIp)
            {
                if (IsZlibStartPacket(data))
                    _libFaolanCompression = new LibFaolanCompression();

                if (_libFaolanCompression == null)
                    throw new Exception("_libFaolanCompression == null");

                var decompressedData = _libFaolanCompression.Inflate(data);
                if (decompressedData == null)
                    throw new Exception("decompressedData == null");
                if (decompressedData.Length == 0)
                    return;

                foreach (var cp in _serverWireProtocol.CreateMessages(decompressedData))
                    _capturedPackets.Add(new PacketWrapper(_packetCounter++, ipv4, tcp.SourcePort, tcp.DestinationPort,
                        (ConanPacket) cp));
            }
            else if (sourceAddress == _clientIp)
            {
                // Client sometimes sends the zlib start packet, but never actually uses compression (anymore)
                if (IsZlibStartPacket(data))
                {
                    Console.WriteLine("Client send zlib start packet");
                    return;
                }

                foreach (var cp in _clientWireProtocol.CreateMessages(data))
                    _capturedPackets.Add(new PacketWrapper(_packetCounter++, ipv4, tcp.SourcePort, tcp.DestinationPort,
                        (ConanPacket) cp));
            }
            else
                throw new Exception("Weird stuff is happening");
        }

        public List<PacketWrapper> Parse()
        {
            _device.Open();
            _device.Capture();
            _device.Close();

            return _capturedPackets;
        }
    }
}