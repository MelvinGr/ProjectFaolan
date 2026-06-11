using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.World;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// 0x96B8DC59 - entity vitals / stat-update packet. The wire builder now lives on World.VitalsPacket (shared
		// with the CombatService); this delegates so the existing SendStatUpdate/SendVitals/BuildVitals callers
		// (login replay, GM, the regen tick) are unchanged.
		internal static byte[] BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint? entityHandle = null)
		{
			return VitalsPacket.BuildStatUpdate(client, stats, entityHandle);
		}

		// Builds the player's own vitals (0x96B8DC59) from the live character's MaxStats - the world-entry replay
		// emits this instead of replaying the captured player's vitals.
		internal static byte[] BuildVitals(INetworkClient client)
		{
			var character = client.Session.Character;
			var stats = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);
			var block = new CharacterStatBlock()
				.Set(LegacyCharacterStatId.HealthMax, stats.Health)
				.Set(LegacyCharacterStatId.StaminaMax, stats.Stamina)
				.Set(LegacyCharacterStatId.ManaMax, stats.Mana);

			return BuildStatUpdate(client, block);
		}

		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint? entityHandle = null)
		{
			return client.SendAsync(BuildStatUpdate(client, stats, entityHandle));
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
			// Values are the character's MAX stats, so emit them under the max ids (matches the retail spawn
			// pattern, which sends max-only and lets the client show full bars).
			var stats = new CharacterStatBlock()
				.Set(LegacyCharacterStatId.HealthMax, health)
				.Set(LegacyCharacterStatId.StaminaMax, stamina)
				.Set(LegacyCharacterStatId.ManaMax, mana);

			return SendStatUpdate(client, stats);
		}
	}
}
