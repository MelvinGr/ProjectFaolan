using System;
using System.Linq;
using System.Net;
using Faolan.Core.Network;

namespace Faolan.PacketAnalyzer
{
	public class PacketSplitter
	{
        public string SourceDestAddress { get; }

        public delegate void ReceivedPacketDel(string ticks, ConanPacket packet);

		private readonly byte[] _packetLengthBuffer = new byte[sizeof(int)];

		private byte[] _packetBuffer;
		private int _packetBytesRead;
		private int _currentPacketCounter = 0;

        public PacketSplitter(string sourceDestAddress)
        {
            SourceDestAddress = sourceDestAddress;
        }

        // https://blog.stephencleary.com/2009/04/message-framing.html
		public void DataReceived(long ticks, byte[] data)
		{
			var i = 0;
			var length = data.Length;
			while (i != length)
			{
				var bytesAvailable = data.Length - i;
				if (_packetBuffer != null)
				{
					// We're reading into the data buffer
					var bytesRequested = _packetBuffer.Length - _packetBytesRead;

					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);
					Array.Copy(data, i, _packetBuffer, _packetBytesRead, bytesTransferred);
					i += bytesTransferred;

					ReadCompleted(ticks, bytesTransferred);
				}
				else
				{
					// We're reading into the length prefix buffer
					var bytesRequested = _packetLengthBuffer.Length - _packetBytesRead;

					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);
					Array.Copy(data, i, _packetLengthBuffer, _packetBytesRead, bytesTransferred);
					i += bytesTransferred;

					ReadCompleted(ticks, bytesTransferred);
				}
			}
		}

		public event ReceivedPacketDel ReceivedPacket;

		private void ReadCompleted(long ticks, int count)
		{
			_packetBytesRead += count;

			if (_packetBuffer == null)
			{
				// We're currently receiving the length buffer
				if (_packetBytesRead == sizeof(int))
				{
					var length = BitConverter.ToInt32(_packetLengthBuffer.Reverse().ToArray(), 0);

					// Sanity check for length < 0
					if (length < 0)
						throw new ProtocolViolationException("Message length is less than zero");

					// Another sanity check is needed here for very large packets, to prevent denial-of-service attacks
					/* if (MaxMessageSize > 0 && length > MaxMessageSize)
					    throw new ProtocolViolationException($"Message length {length} is larger than maximum message size {MaxMessageSize}"); */

					// Zero-length packets are allowed as keepalives
					if (length == 0)
					{
						_packetBytesRead = 0;
						//GotCompletePacket(Array.Empty<byte>());
					}
					else
					{
						// Create the data buffer and start reading into it
						_packetBuffer = new byte[length];
						_packetBytesRead = 0;
					}

                    _currentPacketCounter = 0;

                }
			}
			else
			{
				if (_packetBytesRead == _packetBuffer.Length)
				{
					// We've gotten an entire packet, add back the length
					var completePacket = _packetLengthBuffer.Concat(_packetBuffer).ToArray();

					var packet = new ConanPacket(completePacket);
					//var packet = (TPacket)Activator.CreateInstance(typeof(TPacket), completePacket);
					if (packet?.IsValid == true)
						ReceivedPacket?.Invoke(ticks + "_" + _currentPacketCounter, packet);
					//else
					//    throw new Exception("packet?.IsValid != true");

					// Start reading the length buffer again
					_packetBuffer = null;
					_packetBytesRead = 0;

                    _currentPacketCounter++;

                }
			}
		}
	}
}
