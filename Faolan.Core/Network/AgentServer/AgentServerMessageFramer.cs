using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.AgentServer
{
	public sealed class AgentServerMessageFramer : IMessageFramer
	{
		private byte[] _buffer = [];

		public IEnumerable<byte[]> PushBytes(byte[] bytes, int length)
		{
			var packets = new List<byte[]>();
			_buffer = _buffer.Concat(bytes.Take(length)).ToArray();

			while (_buffer.Length >= sizeof(ushort) * 2)
			{
				var packetLength = (ushort)((_buffer[2] << 8) | _buffer[3]);

				if (packetLength < sizeof(ushort) * 2)
					throw new InvalidOperationException($"Invalid AgentServer packet length: {packetLength}");

				if (_buffer.Length < packetLength)
					break;

				packets.Add(_buffer.Take(packetLength).ToArray());
				_buffer = _buffer.Skip(packetLength).ToArray();
			}

			return packets;
		}
	}
}
