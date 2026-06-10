using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Live world combat state (current vitals per entity handle). In-memory and world-authoritative; never
		// persisted (only CharacterMaxStats is). Shared across the listener's clients.
		private readonly CombatState _combat = new CombatState();

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
			var target = _combat.GetOrAdd(targetHandle, () => EntityVitals.FromMax(CombatResolver.DefaultNpcMaxHealth, 0, CombatResolver.DefaultNpcMaxHealth));

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

		// The melee swing / auto-attack (Interact ActionType 0x0F). Retail's response sequence, reproduced from the
		// strickenbla combat capture, is: a BehaviorControlCenterIIR (0x642CD3D6) acking the behavior (this is what
		// the client blocks on - without it the swing stalls), then a DamageResultIIR (0xFEC4158A) hit, then an
		// EnergyIIR (0x40749B8A) stamina update. The ack header echoes the request (instance, behaviorId, args) with
		// the sequence incremented; its 81-byte result tail is the capture's layout with the live actor=player and
		// the computed damage in the damage slot (the v1/v3/v5 metadata constants are templated - their semantics
		// aren't decoded yet, only that v4 == the swing damage).
		private async Task SendNormalAttackResponse(INetworkClient client, byte[] rawPayload, uint targetHandle)
		{
			// rawPayload = [len4][subop4][c350 4][instance 4][seq 2][behaviorId 4][0000 2][argsLen 2][args][reqTail]
			if (rawPayload.Length < 26)
				return;

			var instance = client.Session.Account.ClientInstance;
			var seq = (ushort)((rawPayload[16] << 8) | rawPayload[17]);
			var argsLen = (ushort)((rawPayload[22] << 8) | rawPayload[23]);
			if (24 + argsLen > rawPayload.Length)
				return;

			var argBytes = rawPayload[24..(24 + argsLen)];
			var reqTail = rawPayload[(24 + argsLen)..];
			var abilityId = reqTail.Length >= 4
				? (uint)((reqTail[^4] << 24) | (reqTail[^3] << 16) | (reqTail[^2] << 8) | reqTail[^1])
				: 0u;

			// Only deal damage to a real enemy: an attack with no target selected (or one that resolves to the
			// player's own handle) must NOT hit the player - that was the "attack hits myself" bug.
			var hasEnemyTarget = targetHandle != 0 && targetHandle != instance;
			var level = client.Session.Character?.Level ?? 1;
			var damage = hasEnemyTarget
				? (uint)CombatResolver.ResolveSpellDamage(abilityId, level)
				: 0u;
			var next = (ushort)(seq + 1);

			// 1) BehaviorControlCenterIIR ack - releases the swing.
			await new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(0x642CD3D6u)
					.WriteUInt32(0x0000C350u)
					.WriteUInt32(instance)
					.WriteUInt16(next)
					.WriteUInt32(0x0000000Fu)
					.WriteUInt16(0)
					.WriteUInt16(argsLen)
					.WriteArray(argBytes)
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(1) // result block: count
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(0)
					.WriteUInt32(0) // entry (6 u32)
					.WriteUInt32(1)
					.WriteUInt32(0x0000C350u)
					.WriteUInt32(instance)
					.WriteUInt32(0) // result: count, c350, actor=player, 0
					.WriteByte(0x06)
					.WriteUInt32(0x000000CDu)
					.WriteUInt32(0)
					.WriteUInt32(1)
					.WriteUInt32(damage)
					.WriteUInt32(2)
					.WriteUInt32(0)
					.WriteUInt32(0))
				.SendAsync(client);

			if (hasEnemyTarget)
			{
				// 2) DamageResultIIR - the hit / floating number.
				await new PacketStream()
					.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
					.WriteArrayUInt32Length(s => s
						.WriteUInt32(0xFEC4158Au)
						.WriteUInt32(0x0000C350u)
						.WriteUInt32(instance)
						.WriteByte(0x00)
						.WriteUInt32(0x0000C350u)
						.WriteUInt32(targetHandle)
						.WriteUInt32(damage)
						.WriteUInt32(2)
						.WriteUInt32(0)
						.WriteUInt32(0))
					.SendAsync(client);

				// Apply to the target's live vitals and push its health bar (0x96B8DC59).
				var target = _combat.GetOrAdd(targetHandle, () => EntityVitals.FromMax(CombatResolver.DefaultNpcMaxHealth, 0, CombatResolver.DefaultNpcMaxHealth));
				target.ApplyHealthDelta(-(int)damage);
				await SendStatUpdate(client, target.ToVitalsBlock(), targetHandle);

				if (target.IsDead)
				{
					Logger.LogInformation("Combat: entity 0x{Target:X8} killed by melee attack from 0x{Caster:X8} (despawn/XP not yet implemented).", targetHandle, instance);
					_combat.Remove(targetHandle);
				}
			}

			// 3) EnergyIIR - the player's stamina after the swing.
			var player = GetOrAddPlayerVitals(client);
			await new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(0x40749B8Au)
					.WriteUInt32(0x0000C350u)
					.WriteUInt32(instance)
					.WriteByte(0x01)
					.WriteUInt32(player.Stamina))
				.SendAsync(client);
		}

		// Spends the caster's mana for a cast and pushes the caster's own 0x96B8DC59 update so the mana bar drops -
		// the spell/mana analogue of the melee swing's stamina cost. (Mana isn't carried by EnergyIIR, which is
		// stamina-only in the captures; it rides the vitals stat block.) Per-spell costs aren't decoded yet, so the
		// cost is provisional. No-op if the player has no mana pool.
		private async Task SpendCasterMana(INetworkClient client, uint spellId)
		{
			var player = GetOrAddPlayerVitals(client);
			if (player.MaxMana == 0)
				return;

			var cost = CombatResolver.ResolveSpellManaCost(spellId, client.Session.Character?.Level ?? 1);
			player.ApplyManaDelta(-(int)cost);
			await SendStatUpdate(client, player.ToVitalsBlock(), client.Session.Account.ClientInstance);
		}
	}
}
