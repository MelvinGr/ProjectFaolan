using System;
using System.Collections.Generic;
using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Packets.ChatServer
{
	public sealed class ChatServerMessageFramer : IMessageFramer
	{
		private byte[] _buffer = [];

		public IEnumerable<byte[]> PushBytes(ReadOnlySpan<byte> bytes)
		{
			var packets = new List<byte[]>();

			// Append this recv to whatever partial frame is still buffered.
			var buffered = new byte[_buffer.Length + bytes.Length];
			_buffer.CopyTo(buffered, 0);
			bytes.CopyTo(buffered.AsSpan(_buffer.Length));
			_buffer = buffered;

			while (_buffer.Length >= sizeof(ushort) * 2)
			{
				// Wire format is [u16 opcode][u16 bodyLength][body]; the length field is the BODY size, so the full
				// packet is bodyLength + the 4-byte header (matches what the server itself sends via
				// WriteUInt16(opcode).WriteArrayUInt16Length(...) and the retail captures).
				var bodyLength = (ushort)((_buffer[2] << 8) | _buffer[3]);
				var totalLength = bodyLength + sizeof(ushort) * 2;

				if (_buffer.Length < totalLength)
					break;

				packets.Add(_buffer[..totalLength]);
				_buffer = _buffer[totalLength..];
			}

			return packets;
		}
	}
}
