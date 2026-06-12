using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Packets.FullCharSnapshot;

namespace Faolan.GameServer.World
{
	// Builds the 0x96B8DC59 entity vitals / stat-update packet - the moment-to-moment counterpart of the big
	// 0x737A6DF9 snapshot's stat block. Confirmed wire layout (docs/archive/quests-and-combat.md):
	//   [subop 96B8DC59][0000C350][entityHandle:4][00000000:4][count:1][count × (statId:u32, value:u32)]
	// Shared by everything that pushes stat/vitals changes (combat, the regen tick, the world-entry replay, GM
	// commands), framed on the world conversation (WorldFraming). Pulled out of the listener so the CombatService can
	// build it without depending on the listener type.
	public static class VitalsPacket
	{
		private const uint VitalsMarker = 0x0000C350;

		// Builds a 0x96B8DC59 stat block for an entity (defaults to the session's own client instance). Returns the
		// wire bytes.
		public static byte[] BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint? entityHandle = null)
		{
			var handle = entityHandle ?? client.Session.Account.ClientInstance;

			var ps = new PacketStream();
			ps
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x00002000)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000RespondsOpcodes.StatUpdateIIR);
					s.WriteUInt32(VitalsMarker);
					s.WriteUInt32(handle);
					s.WriteUInt32(0x00000000);
					stats.WriteTo(s);
				});
			return ps
				.WriteLengthHash()
				.ToArray();
		}
	}
}
