using Faolan.Core.Network;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private static void Send0X200A(INetworkClient client)
		{
			var sender = new byte[] { 0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x54 };
			var receiver = new byte[] { 0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xD4, 0xCB, 0x8B, 0x40 };
			var packetData638167354500717540_1 = new byte[] { 0x00, 0x13, 0xAF, 0x60 };
			new PacketStream()
				.WriteHeader(sender, receiver, null, 0x0000200A, true)
				.WriteArray(packetData638167354500717540_1)
				.Send(client);

			//var sender = new byte[] { 0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x54 };
			//var receiver = new byte[] { 0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xD4, 0xCB, 0x8B, 0x40 };
			var packetData638167354500717540_2 = new byte[] { 0x00, 0x14, 0x2F, 0xD4 };
			new PacketStream()
				.WriteHeader(sender, receiver, null, 0x0000200A, true)
				.WriteArray(packetData638167354500717540_2)
				.Send(client);

			//var sender = new byte[] { 0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x54 };
			//var receiver = new byte[] { 0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xD4, 0xCB, 0x8B, 0x40 };
			var packetData638167354500717540_3 = new byte[] { 0x00, 0x2D, 0xEF, 0xE2 };
			new PacketStream()
				.WriteHeader(sender, receiver, null, 0x0000200A, true)
				.WriteArray(packetData638167354500717540_3)
				.Send(client);

			//var sender = new byte[] { 0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x54 };
			//var receiver = new byte[] { 0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xD4, 0xCB, 0x8B, 0x40 };
			var packetData638167354500717540_4 = new byte[] { 0x00, 0x38, 0xDA, 0x35 };
			new PacketStream()
				.WriteHeader(sender, receiver, null, 0x0000200A, true)
				.WriteArray(packetData638167354500717540_4)
				.Send(client);
			
			//var sender = new byte[] { 0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x54 };
			//var receiver = new byte[] { 0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xD4, 0xCB, 0x8B, 0x40 };
			var packetData638167354500717540_5 = new byte[] { 0x00, 0x3E, 0x33, 0x8F };
			new PacketStream()
				.WriteHeader(sender, receiver, null, 0x0000200A, true)
				.WriteArray(packetData638167354500717540_5)
				.Send(client);

		}
	}
}
