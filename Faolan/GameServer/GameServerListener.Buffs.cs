using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The buff ids owned by a dedicated per-entry re-applier - Shield of the Risen (SendShieldOfTheRisen) and the
		// mount ride buff (RestoreMount). These are re-asserted on EVERY world entry from that flush, so the generic
		// buff persistence must skip them or the client would render a duplicate icon on relog (#274/#221).
		private static readonly HashSet<uint> DedicatedRestoreBuffIds = [ShieldOfTheRisenBase1 << 8, ShieldOfTheRisenBase2 << 8, MountCatalog.RideSpell];

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
			if (session.Character == null)
				return;

			if (session.EnergyMax == 0)
			{
				session.EnergyMax = SpellweaveCatalog.EnergyMaxForClass(session.Character.Class);
				session.Energy = session.EnergyMax;
			}

			if (!session.EnergyLoopActive)
				_ = RunEnergyMirror(client); // fire-and-forget; it logs its own exceptions
		}

		// Shared pet-summon (#307, docs/subsystems/summon-pet-decode.md): spawn the creature, bind it as a REAL pet by emitting
		// PetIIR owner-assign (so the client shows the pet bar + sends QueuePetCommand 0x205b), make it follow, and
		// mark the owner. Used by BOTH the .pet GM command AND the CastSpell hook (casting a PetSummonCatalog spell).
		// `combat` picks the PetIIR kind triple (combat vs social). Returns null if no spawnable template matches.
		internal async Task<WorldEntity?> SummonPetForPlayer(INetworkClient client, string petTemplate, bool combat)
		{
			// The RIGHT model (#244/#307): spawn the pet from its creature TYPE (columns: StatCore + Equipment + Morphs), no world
			// placement. Fall back to the placement path only as a GM-test convenience (`.pet <any world NPC>`), so a
			// creature that hasn't had its stat core lifted to the type yet can still be summoned as a follower.
			var npcType = await Database.GetNpcByName(petTemplate);
			var entity = npcType != null
				? await _broadcaster.SpawnPetForPlayer(client, npcType)
				: await _broadcaster.SpawnNpcForPlayer(client, petTemplate);
			if (entity == null)
				return null;

			var owner = client.Session.Character.Id;
			entity.OwnerHandle = owner;
			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, b =>
				b.WriteArrayUInt32Length(PetIirCodec.BuildAssign(owner, entity.Handle, combat
					? PetIirCodec.CombatKind
					: PetIirCodec.SocialKind))));

			_broadcaster.SetNpcFollow(client, entity.Handle, true);
			return entity;
		}

		// Applies a self-buff (or an offensive MagicBuff cast) by CONSTRUCTING its 54-byte MagicBuffIIR payload from
		// scratch (SelfBuffCodec): the spell's real BASE id (id >> 8, the (base<<8)|rank convention) + its RDB DURATION
		// (Spell.DurationMs -> centiseconds at payload+8), so the client counts down the correct time AND the server
		// expires it at the same instant (#274).
		//
		// The LONG (54-byte) form is the ONLY shape retail ever sends: across all 103 Captures/*.tshark.json the only
		// s2c MagicBuffIIR tails are 54 bytes (x1412) and 0 (x578, the rank-1 end twin) - a 50-byte tail NEVER occurs.
		// The old 50-byte "short form" was our own invention; the client's MagicBuffIIR_c::ReadSubClass runs 4 bytes off
		// the end of it and rejects the record ("datasize left in stream=4, raw=76"), so nothing rendered (#274).
		private async Task ApplySpell(INetworkClient client, uint applyTo, uint applyFrom, uint spellId)
		{
			var spell = await Database.GetSpell(spellId) ?? await Database.GetSpell(spellId >> 8);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, SelfBuffCodec.BuildBuffDataLong(spellId >> 8, durationCs, DefaultSelfBuffEffectNonce));
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

		// Shield of the Risen also drops the instant the player commits an AGGRESSIVE action - an offensive cast, a
		// ground AoE, or a melee/unarmed swing. Retail's newbie protection breaks on the first attack, not only when
		// its timer (1h/10h, expired by TickBuffExpiry) runs out. Removes the tracked shield's icon with the rank-1
		// end twin (the client does NOT self-remove it, same as .cancelbuff), drops it from the live registry, and
		// forgets the session tracking so the next playfield entry re-applies cleanly. No-op when no shield is active
		// (so a plain self-buff cast, which never sets ActiveShieldSpellBase, leaves it untouched).
		private async Task BreakShieldOnAggression(INetworkClient client)
		{
			if (client.Session.ActiveShieldSpellBase is not { } shieldBase)
				return;

			var self = client.Session.Character.Id;
			var appliedId = shieldBase << 8;

			// Retail shows the "lost the grace" on-screen line the instant the shield is LOST to an aggressive action,
			// sent right BEFORE the buff-removal twin (conans-castle-exit rec#508). Without it the icon vanishes with no
			// notification. Only fired here (the aggression loss), not on a playfield swap (SendShieldOfTheRisen).
			await SendFeedback(client, Encoding.Latin1.GetString(FormatFeedbackCodec.BuildLostGraceEffectXml()), FormatFeedbackCodec.EffectLineTypeFlags);
			await SendBuffRemoval(client, self, self, appliedId);

			_simulation.PlayerWorldEntity(client)?.ActiveBuffs.TryRemove(appliedId, out _);
			client.Session.ActiveShieldSpellBase = null;
			Logger.LogInformation("Shield of the Risen broken by aggressive action for {Name}.", client.Session.Character?.Name);
		}

		// Re-applies a player's persisted self-buffs at world entry with their REMAINING time (#274). Reads the
		// CharacterBuff snapshot taken at the last logout/autosave, drops any that lapsed while offline, rebuilds each
		// ApplySpell payload from the persisted base id / effect nonce with a duration recomputed
		// to the time left, and sends it through SendMagicBuffIir - which re-registers the live ActiveBuff so the world
		// tick expires it on schedule and the client counts down the correct remaining time. Runs in the post-0x205F
		// flush beside RestoreMount / SendShieldOfTheRisen, which own their own buffs (excluded at save), so nothing is
		// double-applied. A permanent buff (ExpiresAt == null) restores with a 0 duration (lives until cancelled).
		private async Task RestoreBuffs(INetworkClient client)
		{
			var character = client.Session.Character;

			var buffs = await Database.GetCharacterBuffs(character.Id);
			if (buffs.Length == 0)
				return;

			var now = DateTimeOffset.UtcNow;
			var self = client.Session.Character.Id;
			var restored = 0;

			foreach (var buff in buffs)
			{
				uint remainingCs = 0;
				if (buff.ExpiresAt is { } expiresAt)
				{
					var remaining = (expiresAt - now).TotalSeconds;
					remainingCs = remaining > 0
						? (uint)Math.Min(remaining * 100, uint.MaxValue)
						: 0;
					if (remainingCs == 0)
						continue; // lapsed (or < 1cs left) while logged out - don't re-apply
				}

				var spellBase = buff.BuffId >> 8;
				await SendMagicBuffIir(client, self, self, buff.BuffId, SelfBuffCodec.BuildBuffDataLong(spellBase, remainingCs, buff.Nonce));
				restored++;
			}

			if (restored > 0)
				Logger.LogInformation("Restored {Count} persisted buff(s) for {Name}.", restored, character.Name);
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
					if (session.Account == null)
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
		private async Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			var endId = (appliedSpellId & 0xFFFFFF00u) | 0x01u; // the rank-1 "end" twin of the applied buff id
			var body = NpcSpellCastBuilder.Build(applyTo, applyFrom, endId, []);

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, b => b.WriteArrayUInt32Length(body)));

			// The removal goes to VIEWERS too, for the same reason the application does (SendMagicBuffIir relays it):
			// a buff the other clients were told to start must be told to stop, or their copy of it never ends. The
			// spellweave trance is where this showed: viewers saw the caster enter the weave pose (relayed apply) and
			// then hold it forever, because the fire/cancel removal only ever reached the caster. It is not weave-
			// specific - EVERY buff end was stale on other screens, the weave just made it obvious by being a pose
			// rather than an icon.
			await _broadcaster.BroadcastSpellToViewers(client, body);
		}

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// tools/capture/decode_learn_spell.py, byte-exact in LearnSpellTests). The client pops the "learned a new spell"
		// toast itself. (The sibling DispatchForgetSpell 0x200B / RemoveSpecialAbility 0x2053 / UntrainSingleFeat
		// 0x2011 have no capture oracle yet, so they are not built - see #73.)
		private Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchLearnSpell,
				AbilityGrantBody.BuildLearnSpellBody(spellId)));
		}

		// Sends the MagicBuffIIR (ApplySpell 0x6B87E316) carrier with an already-CONSTRUCTED payload and, for a SELF
		// buff (applyTo == applyFrom), registers it so the server tracks it (.buffs / CancelBuff / tick expiry). The
		// duration is read back from the payload (+8, centiseconds): a known duration (> 0) arms the tick to expire it
		// and send the retail removal twin; 0 = no self-duration (lives until cancelled). Every ApplySpell send path
		// funnels through here - the self-buff, the spellweave trance/component buffs, the mount ride buff and the
		// BCC cast (#69/#274). Offensive casts (applyTo != applyFrom) are damage, handled by ResolveOffensiveCast.
		private async Task SendMagicBuffIir(INetworkClient client, uint applyTo, uint applyFrom, uint spellId, byte[] payload)
		{
			// The ApplySpell carrier body - the SAME layout for player and NPC casts, byte-exact via NpcSpellCastBuilder
			// (NpcSpellCastBuilderTests). Built once so the identical bytes go to the caster AND to nearby viewers below.
			var body = NpcSpellCastBuilder.Build(applyTo, applyFrom, spellId, payload);

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(body)));

			// #48-analogue: nearby players see the cast EFFECT, not just the gesture. Retail streams the actor's
			// ApplySpell to observers who see the caster (other_playersvisible capture: other-player self-buffs +
			// offensive casts, framed to our endpoint). Reframe the same body to each viewer who already sees the caster.
			await _broadcaster.BroadcastSpellToViewers(client, body);

			if (applyTo == applyFrom)
			{
				// #371: only a TIMED self-buff is tracked. A zero-duration spell is an INSTANT effect (the RDB leads:
				// field 0x008 absent == "no duration"), not a permanent buff - it has nothing for TickBuffExpiry to
				// expire, renders no icon, and registering it would leak a never-expiring ActiveBuffs entry that also
				// persists (ExpiresAt NULL) and re-applies on every login. The wire ApplySpell is still SENT above
				// (retail's carrier); we just don't TRACK an instant as an active buff. Every real buff has a finite
				// duration (mount ride 24h, Shield 1h/10h, longest is Barrel Blast 1000h), so nothing legitimate needs
				// the old "lives until cancelled" (null) path. This mirrors the NPC-debuff path (WorldSimulation), which
				// already tracks only when duration > 0.
				var durationMs = ActiveBuff.DurationMsFromPayload(payload);
				if (durationMs > 0)
				{
					var entity = _simulation.PlayerWorldEntity(client);
					if (entity != null && entity.Handle == applyTo)
					{
						entity.ActiveBuffs[spellId] = new ActiveBuff(spellId, applyFrom, DateTimeOffset.UtcNow, durationMs / 1000f)
						{
							// Preserve the effect nonce so the buff can be reconstructed byte-compatibly on relog (#274).
							Nonce = ActiveBuff.NonceFromPayload(payload)
						};
					}
				}
			}
		}

		private async Task SendShieldBuff(INetworkClient client, uint self, uint spellBase)
		{
			var spell = await Database.GetSpell(spellBase);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			var nonce = 0x14000000u | (spellBase & 0x00FFFFFF);
			await SendMagicBuffIir(client, self, self, spellBase << 8, SelfBuffCodec.BuildBuffDataLong(spellBase, durationCs, nonce));
		}

		// Shield of the Risen (#221): retail applies TWO persistent self-buffs on every world entry - both on a fresh
		// login AND on each changemap/teleport re-entry (this runs from FlushPendingPost205FFollowUp, which the teleport
		// path re-arms). Bases 4061768 (1h) + 3666828 (10h); the client renders these TOP-OF-SCREEN (protection banner)
		// unlike the regular corner buffs, and auto-generates the "You are protected by Shield of the Risen" chat line
		// from the ApplySpell itself (no separate server packet - confirmed absent from the capture). Byte-exact against
		// NecroL80_..._conans-castle-exit rec#165 (wire 0x3dfa4800, dur 0x57e40) + rec#736 (0x37f38c00, 0x36ee80); the
		// durations resolve from the Spells rows so SendMagicBuffIir arms the matching expiry (#274) and they DO clear.
		// These use the LONG (54-byte) buff-data form - the client's MagicBuffIIR_c::ReadSubClass rejects the 50-byte
		// short form for them (runs off the end), same as the spellweave/mount buffs.
		//
		// Retail shows exactly ONE shield at a time, picked by playfield type: the 1h base 4061768 on an INSTANCED
		// playfield (the conans-castle login), the 10h base 3666828 in the SHARED open world - the castle-exit
		// transition removes the 1h and applies the 10h (NecroL80_..._castle-exit rec#165 -> rec#735 remove -> rec#736
		// apply). Applying BOTH at once (the first cut) made the client render a second, description-less duplicate.
		// Our maps are all InstanceMode.Shared for now, so this resolves to the 10h everywhere; it future-proofs the
		// moment the beach/Tortage maps are modelled Solo. On a playfield change we remove the prior shield's icon
		// (retail's rank-1 end twin) before applying the new one so only one is ever shown.
		private async Task SendShieldOfTheRisen(INetworkClient client)
		{
			var self = client.Session.Character.Id;
			var map = await Database.GetMap(client.Session.Character.MapId);
			var desired = map?.InstanceMode is null or MapInstanceMode.Shared
				? ShieldOfTheRisenBase2 // 10h shared/open-world
				: ShieldOfTheRisenBase1; // 1h instanced

			if (client.Session.ActiveShieldSpellBase is { } prior && prior != desired)
				await SendBuffRemoval(client, self, self, prior << 8);

			await SendShieldBuff(client, self, desired);
			client.Session.ActiveShieldSpellBase = desired;
		}

		// DispatchUpdateSprintbar (0x200E): the ENERGY / sprint bar update - the yellow bar. Body = [u32 energy][u8 01].
		// This is what actually MOVES the yellow bar (the e9f24184 "Spellweaving Cost" gauge is separate accounting).
		// Retail streams it while weaving to drain the energy bar (decode_spellweave, sender GameCharAgentProject/0x54
		// -> GameCharInterfaceProject/instance; energy full = 8910 for the necro).
		private Task SendSprintBar(INetworkClient client, uint energy)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentProjectType, 0x54), client.Endpoint(Endpoints.GameCharInterfaceProjectType),
				GameCharInterfaceProjectOpcodes.DispatchUpdateSprintBar, b => b.WriteUInt32(energy).WriteByte(0x01)));
		}

		private void TrackSprintEnergy(INetworkClient client, bool sprinting)
		{
			client.Session.IsSprinting = sprinting;
			EnsureEnergyLoop(client);
		}

		// Whether PlayerPersistenceService should persist a live buff for relog (#274): only a SELF-buff (caster is the
		// entity itself) that no dedicated re-applier already owns. NPC-cast debuffs carry a session-volatile caster
		// handle plus a DoT and deliberately do not resume across a relog. Pure predicate - the persistence service
		// (World layer) calls it without otherwise reaching into the listener.
		public static bool ShouldPersistBuff(WorldEntity entity, ActiveBuff buff)
		{
			return buff.CasterHandle == entity.Handle && !DedicatedRestoreBuffIds.Contains(buff.BuffId);
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantability / future
		// level-up), false for the silent per-login re-grant.
		// `sender` defaults to the login/world grant sender (GameCharAgent 0x58); the create-editor world-prep grant
		// rides its own conversation (GameCharAgent 0x0C), so it passes that in - the only thing that differs between
		// the two grant sites (the 81-byte body incl. the TEA key comes wholly from the Ability row either way).
		internal static Task SendAddSpecialAbility(INetworkClient client, Ability ability, bool announce = true, ProtoEndpoint? sender = null)
		{
			return client.SendAsync(new Rpc(sender ?? LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchAddSpecialAbility,
				AbilityGrantBody.BuildAddSpecialAbilityBody(ability, announce)));
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
