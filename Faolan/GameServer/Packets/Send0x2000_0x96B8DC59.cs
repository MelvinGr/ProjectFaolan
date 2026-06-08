using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Packets._0x737A6DF9_Test;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// 0x96B8DC59 - entity vitals / stat-update packet. Pushes a set of stat records to the client for one
		// entity (the moment-to-moment counterpart of the big 0x737A6DF9 snapshot's stat block). Confirmed wire
		// layout from captures (docs/quests-and-combat.md):
		//   [subop 96B8DC59][0000C350][entityHandle:4][00000000:4][count:1][count × (statId:u32, value:u32)]
		// This is what carries combat HP/Stamina/Mana changes; the stat-record encoding is shared with the
		// snapshot via CharacterStatBlock.
		private const uint VitalsMarker = 0x0000C350;

		// Sends a stat block for the given entity handle (defaults to the session's own client instance).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint? entityHandle = null)
		{
			var handle = entityHandle ?? client.Session.Account.ClientInstance;

			return new PacketStream().WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x00002000).WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(GameServer0X2000RespondsOpcodes.Ox96B8Dc59);
				s.WriteUInt32(VitalsMarker);
				s.WriteUInt32(handle);
				s.WriteUInt32(0x00000000);
				stats.WriteTo(s);
			}).SendAsync(client);
		}

		// Pushes the character's own vitals from its persisted MaxStats (or class/level defaults if a legacy
		// character has none). Current==max until per-vital current values are tracked.
		private static Task SendVitals(INetworkClient client)
		{
			var character = client.Session.Character;
			var stats = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);

			return SendVitals(client, stats.Health, stats.Stamina, stats.Mana);
		}

		// Convenience: push explicit vitals (Health/Stamina/Mana). Values are player-facing; the
		// CharacterStatBlock applies the ×100 vitals scale.
		private static Task SendVitals(INetworkClient client, uint health, uint stamina, uint mana)
		{
			var stats = new CharacterStatBlock()
				.Set(LegacyCharacterStatId.Health, health)
				.Set(LegacyCharacterStatId.Stamina, stamina)
				.Set(LegacyCharacterStatId.Mana, mana);

			return SendStatUpdate(client, stats);
		}
	}
}
