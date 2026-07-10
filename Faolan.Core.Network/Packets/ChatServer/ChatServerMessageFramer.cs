using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Packets.ChatServer
{
	public sealed class ChatServerMessageFramer : IMessageFramer
	{
		private byte[] _buffer = [];

		public IEnumerable<byte[]> PushBytes(byte[] bytes, int length)
		{
			var packets = new List<byte[]>();
			_buffer = _buffer.Concat(bytes.Take(length)).ToArray();

			while (_buffer.Length >= sizeof(ushort) * 2)
			{
				// Wire format is [u16 opcode][u16 bodyLength][body]; the length field is the BODY size, so the full
				// packet is bodyLength + the 4-byte header (matches what the server itself sends via
				// WriteUInt16(opcode).WriteArrayUInt16Length(...) and the retail captures).
				var bodyLength = (ushort)((_buffer[2] << 8) | _buffer[3]);
				var totalLength = bodyLength + sizeof(ushort) * 2;

				if (_buffer.Length < totalLength)
					break;

				packets.Add(_buffer.Take(totalLength).ToArray());
				_buffer = _buffer.Skip(totalLength).ToArray();
			}

			return packets;
		}
	}
}
