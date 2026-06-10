using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Network.Interfaces;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Faolan.Core.Network.Conan
{
	public sealed class ConanMessageFramer : IMessageFramer
	{
		private const int MaxPacketSize = 16 * 1024 * 1024;

		private static readonly byte[] InflateStartSequence = [0x80, 0x00, 0x00, 0x05];

		private readonly byte[] _packetLengthBuffer = new byte[sizeof(int)];
		private byte[] _packetBuffer;

		private Inflater? _inflater;
		private int _packetBytesRead;

		public IEnumerable<byte[]> PushBytes(byte[] bytes, int length)
		{
			if (bytes == null)
				throw new ArgumentNullException(nameof(bytes));

			if (length < 0 || length > bytes.Length)
				throw new ArgumentOutOfRangeException(nameof(length));

			bytes = PrepareBytes(bytes, length);
			length = bytes.Length;

			var packets = new List<byte[]>();
			var offset = 0;

			while (offset < length)
			{
				var bytesAvailable = length - offset;

				if (_packetBuffer != null)
				{
					var bytesRequested = _packetBuffer.Length - _packetBytesRead;
					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);

					Array.Copy(bytes, offset, _packetBuffer, _packetBytesRead, bytesTransferred);
					offset += bytesTransferred;
					_packetBytesRead += bytesTransferred;

					if (_packetBytesRead == _packetBuffer.Length)
					{
						packets.Add(_packetLengthBuffer
							.Concat(_packetBuffer)
							.ToArray());
						_packetBuffer = null;
						_packetBytesRead = 0;
					}
				}
				else
				{
					var bytesRequested = _packetLengthBuffer.Length - _packetBytesRead;
					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);

					Array.Copy(bytes, offset, _packetLengthBuffer, _packetBytesRead, bytesTransferred);
					offset += bytesTransferred;
					_packetBytesRead += bytesTransferred;

					if (_packetBytesRead == sizeof(int))
					{
						var packetLength = BitConverter.ToInt32(_packetLengthBuffer
							.Reverse()
							.ToArray(), 0);

						if (packetLength < 0 || packetLength > MaxPacketSize)
						{
							ResetPacketState();
							break;
						}

						if (packetLength == 0)
						{
							_packetBytesRead = 0;
							continue;
						}

						_packetBuffer = new byte[packetLength];
						_packetBytesRead = 0;
					}
				}
			}

			return packets;
		}

		private byte[] PrepareBytes(byte[] bytes, int length)
		{
			if (length == 0)
				return [];

			var input = bytes.Length == length
				? bytes
				: bytes
					.Take(length)
					.ToArray();

			if (HasInflateStartSequence(input))
			{
				_inflater = new Inflater();
				ResetPacketState();

				return input.Length > 9
					? Decompress(_inflater, input
						.Skip(9)
						.ToArray()) ?? []
					: [];
			}

			if (_inflater == null)
				return input;

			return Decompress(_inflater, input) ?? [];
		}

		private void ResetPacketState()
		{
			_packetBuffer = null;
			_packetBytesRead = 0;
		}

		private static byte[]? Decompress(Inflater inflater, byte[] input)
		{
			try
			{
				inflater.SetInput(input);

				using var bos = new MemoryStream(input.Length);
				var buf = new byte[10240];

				while (!inflater.IsFinished && !inflater.IsNeedingInput)
				{
					var count = inflater.Inflate(buf);

					if (count <= 0)
						break;

					bos.Write(buf, 0, count);
				}

				return bos.ToArray();
			}
			catch
			{
				return null;
			}
		}

		private static bool HasInflateStartSequence(byte[] bytes)
		{
			return bytes.Length >= InflateStartSequence.Length && bytes[0] == InflateStartSequence[0] && bytes[1] == InflateStartSequence[1] && bytes[2] == InflateStartSequence[2] && bytes[3] == InflateStartSequence[3];
		}
	}
}
