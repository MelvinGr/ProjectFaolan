using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{

		// The buff-removal end-twin (#274/#253): the same ApplySpell carrier as the apply, rank byte -> 1, empty
		// payload. Routes the private SendBuffRemoval used by .cancelbuff so the world-tick expiry and the GM cancel
		// share one builder.
		Task IWorldOutbound.SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			return SendBuffRemoval(client, applyTo, applyFrom, appliedSpellId);
		}

		// Inits the energy pool + starts the single persistent energy loop (idempotent). Called on sprint and on weave
		// start, so energy is tracked + regenerates regardless of which happens first.
		internal void EnsureEnergyLoop(INetworkClient client)
		{
			var session = client.Session;
			if (session?.Character == null)
				return;

			if (session.EnergyMax == 0)
			{
				session.EnergyMax = SpellweaveCatalog.EnergyMaxForClass(session.Character.Class);
				session.Energy = session.EnergyMax;
			}

			if (!session.EnergyLoopActive)
				_ = RunEnergyMirror(client); // fire-and-forget; it logs its own exceptions
		}

		// Shared pet-summon (#307, docs/summon-pet-decode.md): spawn the creature, bind it as a REAL pet by emitting
		// PetIIR owner-assign (so the client shows the pet bar + sends QueuePetCommand 0x205b), make it follow, and
		// mark the owner. Used by BOTH the .pet GM command AND the CastSpell hook (casting a PetSummonCatalog spell).
		// `combat` picks the PetIIR kind triple (combat vs social). Returns null if no spawnable template matches.
		internal async Task<WorldEntity> SummonPetForPlayer(INetworkClient client, string petTemplate, bool combat)
		{
			// The RIGHT model (#244/#307): spawn the pet from its creature TYPE (Npc.Data lifted stat core), no world
			// placement. Fall back to the placement path only as a GM-test convenience (`.pet <any world NPC>`), so a
			// creature that hasn't had its stat core lifted to the type yet can still be summoned as a follower.
			var npcType = await Database.GetNpcByName(petTemplate);
			var entity = npcType != null
				? await _broadcaster.SpawnPetForPlayer(client, npcType)
				: await _broadcaster.SpawnNpcForPlayer(client, petTemplate);
			if (entity == null)
				return null;

			var owner = client.Session.Account.ClientInstance;
			entity.OwnerHandle = owner;
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(PetIirCodec.BuildAssign(owner, 					entity.Handle, 					combat
						? PetIirCodec.CombatKind
						: PetIirCodec.SocialKind)))
				.SendAsync(client);

			_broadcaster.SetNpcFollow(client, entity.Handle, true);
			return entity;
		}

		// Applies a self-buff (or an offensive MagicBuff cast) by CONSTRUCTING its 50-byte MagicBuffIIR payload from
		// scratch (SelfBuffCodec): the spell's real BASE id (id >> 8, the (base<<8)|rank convention) + its RDB DURATION
		// (Spell.DurationMs -> centiseconds at payload+8), so the client counts down the correct time AND the server
		// expires it at the same instant (#274). Callers that need the 54-byte spellweave/ride shape build that payload
		// themselves and call SendMagicBuffIir directly.
		private async Task ApplySpell(INetworkClient client, uint applyTo, uint applyFrom, uint spellId)
		{
			var spell = await Database.GetSpell(spellId) ?? await Database.GetSpell(spellId >> 8);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, SelfBuffCodec.BuildBuffData(spellId >> 8, durationCs, DefaultSelfBuffEffectNonce));
		}

		// Applies a spellweave buff (trance buff on activate, or a component's own spell on add) with the CORRECT
		// 54-byte MagicBuffIIR blob (SelfBuffCodec.BuildBuffDataLong) instead of the shared 50-byte self-buff form -
		// the client rejects the short shape for these ("MagicBuffIIR_c::ReadSubClass failed"). Looks up the spell's
		// real duration so the client counts it down and SendMagicBuffIir arms the matching expiry. The narrow seam
		// the SpellweaveService uses for every buff it emits.
		private async Task ApplySpellweaveBuff(INetworkClient client, uint applyTo, uint applyFrom, uint spellId)
		{
			var spellBase = spellId >> 8;
			var spell = await Database.GetSpell(spellBase) ?? await Database.GetSpell(spellId);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			var nonce = 0x18000000u | ((uint)Interlocked.Increment(ref _spellweaveEffectSeq) & 0x00FFFFFF);
			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, SelfBuffCodec.BuildBuffDataLong(spellBase, durationCs, nonce));
		}

		// The single per-session energy loop: drains while sprinting, REGENS when idle, and steps aside while weaving
		// (the weave owns the drain + the 0x200E stream). Persistent - it keeps ticking for the whole session so energy
		// recovers after a weave/sprint - and exits only on disconnect. A PeriodicTimer loop, not a self-rescheduling
		// callback. EnergyLoopActive guards against a second loop.
		private async Task RunEnergyMirror(INetworkClient client)
		{
			client.Session.EnergyLoopActive = true;
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(EnergyTickMs));
				while (await timer.WaitForNextTickAsync())
				{
					var session = client.Session;
					if (session?.Account == null)
						return; // disconnected

					if (session.IsSpellweaving)
						continue; // the weave owns the energy this tick

					if (session.IsSprinting)
						session.Energy -= (uint)Math.Min(SprintEnergyDrainPerTick, (int)session.Energy);
					// Exhausted is client-driven for sprint (it owns the visual); nothing to send here.
					else if (session.Energy < session.EnergyMax)
						session.Energy = Math.Min(session.EnergyMax, session.Energy + EnergyRegenPerTick);
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Energy mirror loop failed.");
			}
			finally
			{
				var session = client.Session;
				if (session != null)
					session.EnergyLoopActive = false;
			}
		}

		// The server->client buff REMOVAL packet (#253/#274), decoded from the mount-dismount captures (mount buff end
		// 0x31eb1501 in *_dismount) and the combat captures (NPC-debuff ends 0x31644901 with applyTo=player, 	// applyFrom=NPC): the SAME ApplySpell (0x6B87E316) carrier as the apply, but with the spell id's rank byte set
		// to 1 (the "end" twin) and an EMPTY effect payload. The client clears the buff icon on receipt - so a
		// SERVER-initiated cancel (.cancelbuff), a dispel, or the tick-driven expiry removes the icon live instead of
		// only on relog. applyTo/applyFrom MIRROR the apply: equal for a self-buff, applyTo=player + applyFrom=NPC for
		// an NPC-cast debuff. Byte-exact carrier content (after the IIR [u32 len=0x1A]):
		//   [ApplySpell][c350][applyTo][u16 0][(base<<8)|1][c350][applyFrom]   (no payload)
		private Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			var endId = (appliedSpellId & 0xFFFFFF00u) | 0x01u; // the rank-1 "end" twin of the applied buff id

			return new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(NpcSpellCastBuilder.Build(applyTo, applyFrom, endId, [])))
				.SendAsync(client);
		}

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// Captures/scripts/decode_learn_spell.py, byte-exact in LearnSpellTests). The client pops the "learned a new spell"
		// toast itself. (The sibling DispatchForgetSpell 0x200B / RemoveSpecialAbility 0x2053 / UntrainSingleFeat
		// 0x2011 have no capture oracle yet, so they are not built - see #73.)
		private Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return new PacketStream()
				.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchLearnSpell)
				.WriteArray(AbilityGrantBody.BuildLearnSpellBody(spellId))
				.SendAsync(client);
		}

		// Sends the MagicBuffIIR (ApplySpell 0x6B87E316) carrier with an already-CONSTRUCTED payload and, for a SELF
		// buff (applyTo == applyFrom), registers it so the server tracks it (.buffs / CancelBuff / tick expiry). The
		// duration is read back from the payload (+8, centiseconds): a known duration (> 0) arms the tick to expire it
		// and send the retail removal twin; 0 = no self-duration (lives until cancelled). Every ApplySpell send path
		// funnels through here - the self-buff, the spellweave trance/component buffs, the mount ride buff and the
		// BCC cast (#69/#274). Offensive casts (applyTo != applyFrom) are damage, handled by ResolveOffensiveCast.
		private async Task SendMagicBuffIir(INetworkClient client, uint applyTo, uint applyFrom, uint spellId, byte[] payload)
		{
			await new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(GameServer0X2000Opcodes.MagicBuffIIR)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(applyTo)
					.WriteUInt16(0x0000)
					.WriteUInt32(spellId)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(applyFrom)
					.WriteArray(payload))
				.SendAsync(client);

			if (applyTo == applyFrom)
			{
				var durationMs = ActiveBuff.DurationMsFromPayload(payload);
				var entity = _simulation.PlayerWorldEntity(client);
				if (entity != null && entity.Handle == applyTo)
				{
					entity.ActiveBuffs[spellId] = new ActiveBuff(spellId, 						applyFrom, 						DateTimeOffset.UtcNow, 						durationMs > 0
							? durationMs / 1000.0
							: null);
				}
			}
		}

		// DispatchUpdateSprintbar (0x200E): the ENERGY / sprint bar update - the yellow bar. Body = [u32 energy][u8 01].
		// This is what actually MOVES the yellow bar (the e9f24184 "Spellweaving Cost" gauge is separate accounting).
		// Retail streams it while weaving to drain the energy bar (decode_spellweave, sender GameCharAgentProject/0x54
		// -> GameCharInterfaceProject/instance; energy full = 8910 for the necro).
		private Task SendSprintBar(INetworkClient client, uint energy)
		{
			return new PacketStream()
				.WriteHeader(SprintBarSender, Endpoints.EndpointBytes(Endpoints.GameCharInterfaceProjectType, client.Session.Account.ClientInstance), GameCharInterfaceProjectDispatchOpcodes.DispatchUpdateSprintBar)
				.WriteUInt32(energy)
				.WriteByte(0x01)
				.SendAsync(client);
		}

		private void TrackSprintEnergy(INetworkClient client, bool sprinting)
		{
			if (client.Session?.Character == null)
				return;

			client.Session.IsSprinting = sprinting;
			EnsureEnergyLoop(client);
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantability / future
		// level-up), false for the silent per-login re-grant.
		private static Task SendAddSpecialAbility(INetworkClient client, Ability ability, bool announce = true)
		{
			return new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), (ushort)0x2052).WriteArray(AbilityGrantBody.BuildAddSpecialAbilityBody(ability, announce)).SendAsync(client);
		}

		// Emits the level-up attribute feedback: one AttributeChanged/Increased on-screen line per primary attribute
		// that rose (#209). This is the VISIBLE half of a level-up; the caller still pushes the new vitals maxima.
		// `levelsGained` covers a multi-level jump (e.g. a `.level 40` set). The growth amounts are provisional
		// (Combat.AttributeGrowth) until real per-class tables exist.
		private static async Task SendLevelUpFeedback(INetworkClient client, uint newLevel, uint levelsGained = 1)
		{
			var character = client.Session.Character;
			foreach (var (attributeId, amount) in AttributeGrowth.ForLevelGain(character.Class, newLevel, levelsGained))
				await SendFeedback(client, AttributeChangedFeedback.BuildXml(attributeId, amount), AttributeChangedFeedback.TypeFlags, AttributeChangedFeedback.Footer);
		}
	}
}
