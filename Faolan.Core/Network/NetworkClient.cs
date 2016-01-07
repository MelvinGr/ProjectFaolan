using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Faolan.Core.Data;

namespace Faolan.Core.Network
{
    public abstract class INetworkClient
    {
        protected Socket Socket { get; set; }
        public string IpAddress => ((IPEndPoint) Socket.LocalEndPoint).Address.ToString();

        public Account Account { get; set; }

        public Character Character
        {
            get => Account?.Character;
            set => Account.Character = value;
        }

        public abstract void Send(byte[] value);
    }

    public sealed class NetworkClient<TPacketType> : INetworkClient
        where TPacketType : Packet
    {
        private readonly byte[] _buffer = new byte[10240];
        private readonly Server<TPacketType> _server;

        private byte[] _backBuffer;
        private ConanStream _stream;

        public NetworkClient(Socket client, Server<TPacketType> server)
        {
            Socket = client;
            Account = new Account();
            _server = server;
            BeginReceive();
        }

        private void ReadCallback(int bytesRead)
        {
            if (bytesRead == 0)
                return;

            var receivedBytes = _buffer.Take(bytesRead).ToArray();
            // zlib compression (0x80000005) (Client sends start packet, but does not use compression?)
            if (receivedBytes[0] == 0x80 && receivedBytes[1] == 0x00 &&
                receivedBytes[2] == 0x00 && receivedBytes[3] == 0x05)
                return; // messages;

            if (_backBuffer != null && _backBuffer.Length > 0)
            {
                _stream = new ConanStream(_backBuffer.Concat(receivedBytes).ToArray());
                _backBuffer = null;
            }
            else
            {
                _stream = new ConanStream(receivedBytes);
            }

            while (true)
            {
                if (_stream == null || _stream.Length == 0)
                    break;

                _stream.Position = 0;

                var packet = (TPacketType) Activator.CreateInstance(typeof(TPacketType), _stream);
                if (packet.Valid)
                {
                    _server.ReceivedPacket(this, packet);

                    Functions.TrimStream(ref _stream);
                    break;
                }

                _backBuffer = _stream.ToArray();
            }

            BeginReceive();
        }

        public override void Send(byte[] value)
        {
            Socket.BeginSend(value, 0, value.Length, 0, ar =>
            {
                try
                {
                    Socket.EndSend(ar);
                }
                catch //(Exception e)
                {
                    _server.ClientDisconnected(this);
                    Socket = null;
                    //Console.WriteLine(e.Message);
                }
            }, null);
        }

        private void BeginReceive()
        {
            Socket.BeginReceive(_buffer, 0, _buffer.Length, 0, ar =>
            {
                try
                {
                    ReadCallback(Socket.EndReceive(ar));
                }
                catch //(Exception e)
                {
                    _server.ClientDisconnected(this);
                    Socket = null;
                    //Console.WriteLine(e.Message);
                }
            }, null);
        }
    }
}