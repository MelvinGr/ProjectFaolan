using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
	public interface INetworkClient
	{
		string IpAddress { get; }
		Account Account { get; set; }
		Character Character { get; }

		void Send(byte[] value);
	}

	public abstract class NetworkClient : INetworkClient
	{
		public delegate void NetworkClientDelegate(INetworkClient client);

		public delegate Task ReceivedPacketDelegate(INetworkClient client, Packet packet);

		protected readonly ILogger Logger;
		protected Socket Socket;

		public ReceivedPacketDelegate ReceivedPacket { get; set; }
		public NetworkClientDelegate Disconnected { get; set; }

		protected NetworkClient(Socket socket, ILogger logger)
		{
			Socket = socket;
			Logger = logger;
		}

		public string IpAddress => ((IPEndPoint)Socket.RemoteEndPoint)?.Address.ToString();

		public Account Account { get; set; }
		public Character Character => Account?.Character;

		public abstract void Send(byte[] value);
		public abstract void Start();
	}

	public class NetworkClient<TPacket> : NetworkClient
		where TPacket : Packet
	{
		private readonly object _lock = new();
		private readonly byte[] _packetLengthBuffer = new byte[sizeof(int)];
		private readonly byte[] _tcpBuffer = new byte[0xFFFF];

		private byte[] _packetBuffer;
		private int _packetBytesRead;

		public NetworkClient(Socket socket, ILogger logger)
			: base(socket, logger)
		{
		}

		public override void Start()
		{
			BeginReceive( /*token*/);
		}

		public override void Send(byte[] value)
		{
			/*var stream = new ConanStream(value);
			_ = stream.ReadUInt32();
			_ = stream.ReadUInt32();
			_ = stream.ReadUInt32();
			_ = stream.ReadByte();
			_ = stream.ReadArrayPrependLengthByte();
			_ = stream.ReadByte();
			_ = stream.ReadArrayPrependLengthByte();
			var opcode = stream.ReadUInt16();            
			Console.WriteLine($"Send opcode: 0x{opcode:X4}");*/

			lock (_lock)
			{
				try
				{
					Socket?.Send(value);

					/*Socket?.BeginSend(value, 0, value.Length, 0, ar =>
					{
					    try
					    {
					        Socket.EndSend(ar);
					    }
					    catch // (Exception e)
					    {
					        Disconnected?.Invoke(this);
					        Socket = null;
					    }
					}, null);*/
				}
				catch // (Exception e)
				{
					Disconnected?.Invoke(this);
					Socket = null;
				}
			}
		}

		private void BeginReceive( /*token*/)
		{
			try
			{
				Socket?.BeginReceive(_tcpBuffer, 0, _tcpBuffer.Length, 0, ar =>
				{
					try
					{
						var bytesRead = Socket.EndReceive(ar);
						if (bytesRead == 0)
							return;

						var buffer = _tcpBuffer.Take(bytesRead).ToArray();

						// zlib compression (0x80000005)
						if (buffer[0] == 0x80 && buffer[1] == 0x00 && buffer[2] == 0x00 && buffer[3] == 0x05)
						{
							//decompressor = new Inflater();
							//buffer = buffer.Skip(9).ToArray();
							//Console.WriteLine("zlib start");

							Logger.LogDebug("ZLIB: " + buffer.ToHex());

							BeginReceive();
							return;
						}

						DataReceived(buffer, bytesRead);
						BeginReceive();
					}
					catch // (Exception e)
					{
						Disconnected?.Invoke(this);
						Socket = null;
					}
				}, null);
			}
			catch // (Exception e)
			{
				Disconnected?.Invoke(this);
				Socket = null;
			}
		}

		// https://blog.stephencleary.com/2009/04/message-framing.html
		private void DataReceived(byte[] data, int length)
		{
			var i = 0;
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

					ReadCompleted(bytesTransferred);
				}
				else
				{
					// We're reading into the length prefix buffer
					var bytesRequested = _packetLengthBuffer.Length - _packetBytesRead;

					var bytesTransferred = Math.Min(bytesRequested, bytesAvailable);
					Array.Copy(data, i, _packetLengthBuffer, _packetBytesRead, bytesTransferred);
					i += bytesTransferred;

					ReadCompleted(bytesTransferred);
				}
			}
		}

		private void ReadCompleted(int count)
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
				}
			}
			else
			{
				if (_packetBytesRead == _packetBuffer.Length)
				{
					// We've gotten an entire packet, add back the length
					var completePacket = _packetLengthBuffer.Concat(_packetBuffer).ToArray();

					var packet = (TPacket)Activator.CreateInstance(typeof(TPacket), completePacket);
					if (packet?.IsValid == true)
						ReceivedPacket?.Invoke(this, packet);
					else
						throw new Exception("packet?.IsValid != true");

					// Start reading the length buffer again
					_packetBuffer = null;
					_packetBytesRead = 0;
				}
			}
		}
	}
}
