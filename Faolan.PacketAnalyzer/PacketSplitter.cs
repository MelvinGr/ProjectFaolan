using System;
using System.Linq;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;

namespace Faolan.PacketAnalyzer
{
	public class PacketSplitter
	{
		private const int InStreamMarkerTailLength = 5;
		private const int MaxPacketSize = 16 * 1024 * 1024;

		// In-band marker that appears INSIDE the decompressed GameServer stream between packet groups:
		// 0x80000005 followed by 5 more bytes (9 bytes total). It is the same byte sequence that starts
		// compression, but here it is literal stream data, not a packet length - it must be skipped.
		private static readonly byte[] InStreamMarkerPrefix = [0x80, 0x00, 0x00, 0x05];
		public ReceivedPacketDel? ReceivedPacket;
		private readonly byte[] _packetLengthBuffer = new byte[sizeof(int)];

		private readonly PacketFramingMode _framingMode;
		private byte[] _inlineBuffer = [];
		private byte[]? _packetBuffer;
		private int _currentPacketCounter;
		private int _markerTailRemaining;
		private int _packetBytesRead;
		private long? _currentTicks;

		public string SourceDestAddress { get; }

		public delegate void ReceivedPacketDel(string ticks, Packet packet);

		public PacketSplitter(string sourceDestAddress, PacketFramingMode framingMode = PacketFramingMode.LengthPrefixed)
		{
			SourceDestAddress = sourceDestAddress;
			_framingMode = framingMode;
		}

		// https://blog.stephencleary.com/2009/04/message-framing.html
		public void DataReceived(long ticks, byte[] data)
		{
			if (data == null || data.Length == 0)
				return;

			if (_currentTicks != ticks)
			{
				_currentTicks = ticks;
				_currentPacketCounter = 0;
			}

			if (_framingMode == PacketFramingMode.AgentServerInline)
			{
				ReadInlinePackets(ticks, data);
				return;
			}

			var i = 0;
			var length = data.Length;
			while (i != length)
			{
				var bytesAvailable = length - i;

				// Skip the trailing bytes of an in-band 0x80000005 marker (see ReadCompleted).
				if (_markerTailRemaining > 0)
				{
					var skip = Math.Min(_markerTailRemaining, bytesAvailable);
					i += skip;
					_markerTailRemaining -= skip;
					continue;
				}

				if (_packetBuffer != null)
				{
					// We're reading into the data buffer
					var bytesRequested = _packetBuffer.Length - _packetBytesRead;

					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);
					Array.Copy(data, i, _packetBuffer, _packetBytesRead, bytesTransferred);
					i += bytesTransferred;

					if (!ReadCompleted(ticks, bytesTransferred))
						return;
				}
				else
				{
					// We're reading into the length prefix buffer
					var bytesRequested = _packetLengthBuffer.Length - _packetBytesRead;

					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);
					Array.Copy(data, i, _packetLengthBuffer, _packetBytesRead, bytesTransferred);
					i += bytesTransferred;

					if (!ReadCompleted(ticks, bytesTransferred))
						return;
				}
			}
		}

		private bool ReadCompleted(long ticks, int count)
		{
			_packetBytesRead += count;

			if (_packetBuffer == null)
			{
				// We're currently receiving the length buffer
				if (_packetBytesRead == sizeof(int))
				{
					// In-band 0x80000005 marker (9 bytes) inside the decompressed GameServer stream: it is
					// stream framing, not a packet. Skip its 5 trailing bytes and keep going - otherwise the
					// whole rest of the stream (e.g. the create->world intro cutscene) is dropped.
					if (_packetLengthBuffer[0] == InStreamMarkerPrefix[0] && _packetLengthBuffer[1] == InStreamMarkerPrefix[1] && _packetLengthBuffer[2] == InStreamMarkerPrefix[2] &&
					    _packetLengthBuffer[3] == InStreamMarkerPrefix[3])
					{
						_markerTailRemaining = InStreamMarkerTailLength;
						_packetBytesRead = 0;
						return true;
					}

					var length = BitConverter.ToInt32(_packetLengthBuffer
						.Reverse()
						.ToArray(), 0);

					// Sanity check for length < 0
					if (length < 0)
					{
						ResetPacketState();
						return false;
					}

					// Another sanity check is needed here for very large packets, to prevent denial-of-service attacks
					if (length > MaxPacketSize)
					{
						ResetPacketState();
						return false;
					}

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
				}
			}
			else
			{
				var packetBuffer = _packetBuffer;
				if (packetBuffer == null)
					return false;

				if (_packetBytesRead == packetBuffer.Length)
				{
					// We've gotten an entire packet, add back the length
					var completePacket = _packetLengthBuffer
						.Concat(packetBuffer)
						.ToArray();

					Packet? packet = null;
					try
					{
						packet = new ConanPacket(completePacket);
						//var packet = (TPacket)Activator.CreateInstance(typeof(TPacket), completePacket);
					}
					catch //(Exception e)
					{
						try
						{
							packet = new AgentServerPacket(completePacket);
						}
						catch
						{
							//
						}
					}

					if (packet?.IsValid == true)
						ReceivedPacket?.Invoke(ticks + "_" + _currentPacketCounter, packet);
					else
					{
						ResetPacketState();
						return false;
					}

					// Start reading the length buffer again
					_packetBuffer = null;
					_packetBytesRead = 0;

					_currentPacketCounter++;
				}
			}

			return true;
		}

		private void ReadInlinePackets(long ticks, byte[] data)
		{
			// AgentServer packets ([u16 opcode][u16 bodyLength][body]) can span TCP segments, so buffer the leftover
			// bytes between calls instead of dropping a partial packet (which desynced the stream and fed a garbage
			// length to the parser).
			_inlineBuffer = _inlineBuffer.Length == 0
				? data
				: _inlineBuffer
					.Concat(data)
					.ToArray();

			var offset = 0;
			while (offset + sizeof(ushort) * 2 <= _inlineBuffer.Length)
			{
				var bodyLength = (ushort)((_inlineBuffer[offset + 2] << 8) | _inlineBuffer[offset + 3]);
				var totalPacketLength = bodyLength + sizeof(ushort) * 2;
				if (offset + totalPacketLength > _inlineBuffer.Length)
					break; // incomplete packet - wait for the next segment

				if (bodyLength > 0)
				{
					var packetBytes = new byte[totalPacketLength];
					Array.Copy(_inlineBuffer, offset, packetBytes, 0, totalPacketLength);
					var packet = new AgentServerPacket(packetBytes);
					if (packet.IsValid)
					{
						ReceivedPacket?.Invoke(ticks + "_" + _currentPacketCounter, packet);
						_currentPacketCounter++;
					}
				}

				offset += totalPacketLength;
			}

			_inlineBuffer = offset == 0
				? _inlineBuffer
				: _inlineBuffer[offset..];
		}

		private void ResetPacketState()
		{
			_packetBuffer = null;
			_packetBytesRead = 0;
		}
	}

	public enum PacketFramingMode
	{
		LengthPrefixed,
		AgentServerInline
	}
}
