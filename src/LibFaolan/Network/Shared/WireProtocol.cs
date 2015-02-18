using System;
using System.Collections.Generic;
using System.IO;
using Hik.Communication.Scs.Communication.Messages;
using Hik.Communication.Scs.Communication.Protocols;

// https://github.com/conan513/TeraEmulator/blob/master/Network/WireProtocol/GameProtocol.cs

namespace LibFaolan.Network.Shared
{
    public class WireProtocol<T> : IScsWireProtocol
        where T : Packet
    {
        private byte[] _backBuffer;
        //private LibFaolanCompression _libFaolanCompression;
        private MemoryStream _stream;

        public WireProtocol() //bool enableCompression)
        {
            EnableCompression = false; //enableCompression;
        }

        public bool EnableCompression { get; }

        public IEnumerable<IScsMessage> CreateMessages(byte[] receivedBytes)
        {
            var messages = new List<IScsMessage>();

            // zlib compression (0x80000005)
            if (!EnableCompression && receivedBytes.Length == 9 &&
                (receivedBytes[0] == 0x80 && receivedBytes[1] == 0x00 &&
                 receivedBytes[2] == 0x00 && receivedBytes[3] == 0x05))
            {
                //Console.WriteLine("Skipping zlib packet");
                return messages;
            }

            /*if (EnableCompression)
            {
                // zlib compression (0x80000005)
                if (receivedBytes[0] == 0x80 && receivedBytes[1] == 0x00 &&
                    receivedBytes[2] == 0x00 && receivedBytes[3] == 0x05)
                {
                    _libFaolanCompression = new LibFaolanCompression();
                }

                if (_libFaolanCompression != null)
                {
                    if (_backBuffer != null)
                    {
                        //_stream.Write(_backBuffer, 0, _backBuffer.Length);
                        receivedBytes = _backBuffer.Concat(receivedBytes).ToArray();
                        _backBuffer = null;
                    }

                    var outputBuffer = _libFaolanCompression.Inflate(receivedBytes);
                    if (outputBuffer != null)
                    {
                        _stream = new MemoryStream();
                        _stream.Write(outputBuffer, 0, outputBuffer.Length);
                    }
                }
                else
                    HandleNormal(receivedBytes);
            }
            else*/
            HandleNormal(receivedBytes);

            while (ReadMessage(messages)) ;
            return messages;
        }

        public byte[] GetBytes(IScsMessage message)
        {
            if (message is ScsPingMessage)
                return null;

            return ((Packet) message).ToArray();
        }

        public void Reset()
        {
            //throw new NotImplementedException();
        }

        private void HandleNormal(byte[] receivedBytes)
        {
            if (_backBuffer != null)
            {
                _stream = new MemoryStream();
                _stream.Write(_backBuffer, 0, _backBuffer.Length);
                _stream.Write(receivedBytes, 0, receivedBytes.Length);
                _backBuffer = null;
            }
            else
                _stream = new MemoryStream(receivedBytes);
        }

        private bool ReadMessage(ICollection<IScsMessage> messages)
        {
            if (_stream == null || _stream.Length == 0)
                return false;

            _stream.Position = 0;

            var packet = (T) Activator.CreateInstance(typeof (T), new PacketBuffer(_stream));
            if (packet.Length != UInt32.MaxValue)
            {
                messages.Add(packet);
                Functions.TrimStream(ref _stream);
                return true;
            }

            _backBuffer = _stream.ToArray();
            return false;
        }
    }
}