using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Live world combat state (current vitals per entity handle). In-memory and world-authoritative; never
		// persisted (only CharacterMaxStats is). Shared across the listener's clients.
		private readonly CombatState _combat = new();

		// The player's own live vitals, seeded from CharacterMaxStats (or class/level defaults) on first use.
		private EntityVitals GetOrAddPlayerVitals(INetworkClient client)
		{
			var character = client.Session.Character;
			return _combat.GetOrAdd(client.Session.Account.ClientInstance, () =>
			{
				var max = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);
				return EntityVitals.FromMax(max.Health, max.Mana, max.Stamina);
			});
		}

		// Resolves an offensive cast: applies damage to the target's live vitals and pushes a 0x96B8DC59 update so
		// its health bar moves. The ApplySpell cast itself is broadcast by the caller. Death handling (despawn + XP)
		// is a TODO - the death/despawn packet isn't decoded yet (docs/quests-and-combat.md).
		private async Task ResolveOffensiveCast(INetworkClient client, uint casterHandle, uint targetHandle, uint spellId)
		{
			var target = _combat.GetOrAdd(targetHandle,
				() => EntityVitals.FromMax(CombatResolver.DefaultNpcMaxHealth, 0, CombatResolver.DefaultNpcMaxHealth));

			var casterLevel = client.Session.Character?.Level ?? 1;
			var damage = CombatResolver.ResolveSpellDamage(spellId, casterLevel);
			target.ApplyHealthDelta(-damage);

			await SendStatUpdate(client, target.ToVitalsBlock(), targetHandle);

			if (target.IsDead)
			{
				Logger.LogInformation("Combat: entity 0x{Target:X8} killed by spell {SpellId} cast from 0x{Caster:X8} (despawn/XP not yet implemented).", targetHandle, spellId, casterHandle);
				_combat.Remove(targetHandle);
			}
		}
	}
}
