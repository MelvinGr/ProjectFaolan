using Faolan.Core.Constants;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
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

		// The level that fills a buff payload's @26/@42 words (SelfBuffCodec): the CASTER's, which for every path here
		// is the player. Was a hardcoded 80 - right only for a level-80 character (#438).
		private static uint CasterLevel(INetworkClient client)
		{
			return client.Session.Character?.Level ?? 1;
		}

		// ISpellEffectOutbound (#439) - the three wire ops a SpellEffect row needs, so SpellEffectService dispatches
		// data-driven effects without holding the listener. All three already existed for their original callers; this
		// only names them as one seam.
		Task ISpellEffectOutbound.ApplySelfBuff(INetworkClient client, uint wireSpellId)
		{
			var self = client.Session.Character.Id;
			return ApplySpell(client, self, self, wireSpellId);
		}

		async Task<bool> ISpellEffectOutbound.SummonPet(INetworkClient client, string template, bool combat)
		{
			return await SummonPetForPlayer(client, template, combat) != null;
		}

		Task ISpellEffectOutbound.SummonTimedPet(INetworkClient client, string template, TimeSpan duration, TimeSpan? cap)
		{
			return SummonTimedPetForPlayer(client, template, duration, cap);
		}

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
		// mark the owner. Used by BOTH the .pet GM command AND the CastSpell hook (casting a spell with a SummonPet effect).
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
			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, b =>
				b.WriteArrayUInt32Length(PetCodec.Build(PetBindings.Assign(owner, entity.Handle, combat
					? PetCodec.CombatKind
					: PetCodec.SocialKind)))));

			_broadcaster.SetNpcFollow(client, entity.Handle, true);
			return entity;
		}

		// Summons a pet that lives for `duration`, or EXTENDS the one already out. Frozen Hatred's fatality pet is the
		// shape this serves (#140): "the Frozen Hatred pet appears and is under your control for 60 seconds. If you
		// already have a Frozen Hatred pet, the duration of the pet will increase by another 60 seconds. This timer is
		// capped at 5 minutes." (in-client tooltip). One field carries both halves - DespawnAt is pushed out on each
		// fatality and clamped to the cap, and the world tick fades whatever it finds expired.
		internal async Task SummonTimedPetForPlayer(INetworkClient client, string petTemplate, TimeSpan duration, TimeSpan? cap)
		{
			var owner = _simulation.PlayerWorldEntity(client);
			if (owner == null)
				return;

			var now = DateTimeOffset.UtcNow;
			var existing = _simulation.World.ZoneForEntity(owner).Entities.FirstOrDefault(e => e.OwnerHandle == owner.Handle && e.Name == petTemplate && e.DespawnAt != null);
			if (existing != null)
			{
				var extended = (existing.DespawnAt!.Value > now
					? existing.DespawnAt.Value
					: now) + duration;
				existing.DespawnAt = cap is { } ceiling && extended > now + ceiling
					? now + ceiling
					: extended;
				Logger.LogInformation("Timed pet '{Pet}' for '{Player}' extended to {Seconds:F0}s remaining (cap {Cap}).", petTemplate, client.Session.Character?.Name,
					(existing.DespawnAt.Value - now).TotalSeconds, cap is { } c
						? $"{c.TotalSeconds:F0}s"
						: "none");
				return;
			}

			var pet = await SummonPetForPlayer(client, petTemplate, true);
			if (pet == null)
			{
				// No creature type by that name: say so rather than silently granting nothing - the buff that asked for
				// it is already on the player, so a missing template is a content gap, not a state.
				Logger.LogWarning("Timed pet '{Pet}' could not be summoned for '{Player}': no NPC type by that name.", petTemplate, client.Session.Character?.Name);
				return;
			}

			pet.DespawnAt = now + duration;
			Logger.LogInformation("Timed pet '{Pet}' summoned for '{Player}' for {Seconds:F0}s.", petTemplate, client.Session.Character?.Name, duration.TotalSeconds);
		}

		// Applies a self-buff (or an offensive MagicBuff cast) by CONSTRUCTING its 54-byte MagicBuffIIR payload from
		// scratch (SelfBuffCodec): the spell's real BASE id (id >> 8; the low byte is the client's FORM selector, #438) + its RDB DURATION
		// (Spell.DurationMs -> centiseconds at payload+8), so the client counts down the correct time AND the server
		// expires it at the same instant (#274).
		//
		// The LONG (54-byte) form is the ONLY shape retail ever sends: across all 111 Captures/*.tshark.json the only
		// s2c MagicBuffIIR tails are 54 bytes (x1938) and 0 (x982, the END twin) - a 50-byte tail NEVER occurs.
		// The old 50-byte "short form" was our own invention; the client's MagicBuffIIR_c::ReadSubClass runs 4 bytes off
		// the end of it and rejects the record ("datasize left in stream=4, raw=76"), so nothing rendered (#274).
		private async Task ApplySpell(INetworkClient client, uint applyTo, uint applyFrom, uint spellId)
		{
			var spell = await Database.GetSpell(spellId) ?? await Database.GetSpell(spellId >> 8);

			// A cast spell can carry no effect of its own and name a SEPARATE record that is the actual buff - the
			// castable is the trigger, the EffectSpell is what lands. Frozen Hatred is the proven case (#434): the
			// necro casts 4377207, whose row has no duration at all, and retail answers with an ApplySpell for
			// 4377209 - a distinct id, same name, carrying the 27.7h duration and the cold-effect resource
			// (xxx_mage-necromancer beach->city, cast fr5393 -> ApplySpell 0x42CA7900 fr5400). Applying the CAST id
			// meant a zero-duration record that renders no icon and tracks no buff, i.e. casting it did nothing.
			if (spell is { DurationMs: 0, EffectSpell: > 0 } && await Database.GetSpell(spell.EffectSpell) is { } effect)
			{
				var effectWireId = effect.Id << 8;
				Logger.LogInformation("Cast {Cast} ('{Name}') carries no effect of its own; applying its effect spell {Effect} ('{EffectName}').", spell.Id, spell.Name, effect.Id, effect.Name);
				await SendMagicBuffIir(client, applyTo, applyFrom, effectWireId, MagicBuffDatas.ApplyNow(effect.Id, effect.DurationMs / 10, CasterLevel(client)));
				await ApplyPoolEffects(client, applyTo, effect.Id);
				return;
			}

			var durationCs = (spell?.DurationMs ?? 0) / 10;

			// ⚠️ RETAIL SENDS NO ApplySpell FOR A SPELL WITH NOTHING TO APPLY, and sending one anyway is what broke
			// spell cooldowns (#767). Measured over all 132 dumps: of 5,423 s2c MagicBuffIIR bodies, 5,416 carry a
			// spell with a real duration and SEVEN carry a zero-duration one - and those seven are `Armor of Faith`
			// (x4), `Emerald Aptitude` (x2) and `Resting`, i.e. genuine buffs whose duration is missing on OUR side,
			// not damage casts. Chill, Ice Strike, Lifestrike and Pestilential Blast - the nukes cast constantly
			// throughout the corpus - never appear once.
			//
			// What it costs to send one anyway, in the client's own words (in-client 2026-08-09, channel
			// GameCode/Magic at ERROR):
			//
			//     CoreMagicHolder::ActivateBuff 3666380 remaining time 0
			//     SpellTemplate_t::GetMagicSpell failed to load spell with id 14321      (= 3666380 >> 8)
			//
			// ActivateBuff ABORTS on a zero remaining time, and the spell's own GameActions - which for `Chill` is
			// exactly one element, `SetSpellLineRecastTime(41028, 400)` - never execute. That is why the cooldown
			// never started: the arm is real and lives inside this path. The same log shows it working on a spell
			// that DOES have a duration, arming a whole line at once:
			//
			//     GameCode/Magic/Recast - Setting recast timer on spell: 144674..144677, 1206011 ... to: 600
			//
			// So the send is gated on there being something to apply. The old code sent unconditionally and called it
			// "retail's carrier"; that was never measured, and the measurement says otherwise.
			if (durationCs == 0)
			{
				Logger.LogDebug("Cast {Spell} has no duration and no effect spell - no ApplySpell sent (retail sends none; a zero-duration one aborts the client's ActivateBuff and eats the spell's recast).", spellId >> 8);
				await ApplyPoolEffects(client, applyTo, spellId >> 8); // an INSTANT pool move still lands (#702)
				return;
			}

			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, MagicBuffDatas.ApplyNow(spellId >> 8, durationCs, CasterLevel(client)));
			await ApplyPoolEffects(client, applyTo, spellId >> 8);
		}

		// The INSTANT pool move a landing spell carries (#702) - a heal, a mana restore, a stamina refill. Applied
		// here rather than in the buff engine because it is not a lasting stat: it happens once, when the spell lands.
		//
		// ⚠️ ONCE is the whole difficulty. These rows sit beside the lasting deltas on the same spell, and the buff
		// paths that touch a spell repeatedly - the relog RESTORE, the expiry sweep, a re-apply that refreshes a
		// duration - must not carry them. So this hangs off ApplySpell, which is "a spell just landed on someone",
		// and NOT off SendMagicBuffIir, which every one of those paths goes through.
		//
		// Only a PLAYER's own pools, for now: every one of the 52 class-castable carriers is a self-heal or a
		// team-support spell landing on a player, and an NPC's pools have their own path (ApplyNpcDamage) with its own
		// death lifecycle that a heal must not bypass.
		private async Task ApplyPoolEffects(INetworkClient client, uint applyTo, uint spellBase)
		{
			if (applyTo != client.Session.Character.Id)
				return;

			var effects = await _poolEffects.ForSpellBase(spellBase);
			if (effects.Count == 0)
				return;

			var vitals = _combat.GetOrAddPlayerVitals(client);
			var moved = false;
			foreach (var effect in effects)
			{
				var max = effect.Pool switch
				{
					CharacterStatId.HealthCurrent => vitals.MaxHealth,
					CharacterStatId.ManaCurrent => vitals.MaxMana,
					_ => vitals.MaxStamina
				};

				var amount = effect.For(max);
				if (amount == 0 || max == 0)
					continue;

				switch (effect.Pool)
				{
					case CharacterStatId.HealthCurrent:
					{
						vitals.ApplyHealthDelta(amount);
						break;
					}

					case CharacterStatId.ManaCurrent:
					{
						vitals.ApplyManaDelta(amount);
						break;
					}

					default:
					{
						vitals.Stamina = (uint)Math.Clamp((long)vitals.Stamina + amount, 0, max);
						break;
					}
				}

				moved = true;
				Logger.LogInformation("Spell {Spell} moved {Name}'s {Pool} by {Amount}.", spellBase, client.Session.Character.Name, effect.Pool, amount);
			}

			if (moved)
				await SendStatUpdate(client, vitals.ToCurrentsBlock(), client.Session.Character.Id);
		}

		// Applies a spellweave buff (trance buff on activate, or a component's own spell on add) with the CORRECT
		// 54-byte MagicBuffIIR blob (SelfBuffCodec.BuildBuffDataLong) instead of the shared 50-byte self-buff form -
		// the client rejects the short shape for these ("MagicBuffIIR_c::ReadSubClass failed"). Looks up the spell's
		// real duration so the client counts it down and SendMagicBuffIir arms the matching expiry. The narrow seam
		// the SpellweaveService uses for every buff it emits.
		private async Task ApplyBuffById(INetworkClient client, uint applyTo, uint applyFrom, uint spellId)
		{
			var spellBase = spellId >> 8;
			var spell = await Database.GetSpell(spellBase) ?? await Database.GetSpell(spellId);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, MagicBuffDatas.ApplyNow(spellBase, durationCs, CasterLevel(client)));
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
			await SendFeedback(client, new FeedbackLiteral(FeedbackXml.LostGraceEffect, FormatFeedbackCodec.EffectLineTypeFlags));
			await SendBuffRemoval(client, self, self, appliedId); // deregisters the live buff too

			await SyncBuffStats(client);
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
				// Duration is the REMAINING time, not the spell's full duration, because THAT IS WHAT RETAIL SENDS
				// (#601). Measured over the corpus with `--report buff-durations`, which prints each spell's catalogue
				// duration beside every duration retail actually put on the wire: 51 of 256 spells carry at least one
				// below-full value, and they are exactly the long ones a session can span - `Contract of Protection`
				// (full 1,440,000 cs) appears as 1,180,241 / 1,195,185 / 1,213,465 / ... / 1,440,000, `XP Boost (4h)`
				// as 1,268,055, `Critical Rating Boost (4h)` as 431,043. Short buffs (Wrath 1000, Chill 600) only ever
				// send full, because they are never alive across a restore.
				//
				// ⚠️ So do NOT "improve" this into sending the full duration plus an absolute expiry and letting the
				// client shorten it. That shape is not on the wire anywhere in the corpus, and #601 was closed on this
				// measurement rather than implemented. The stamp is still sourced from the same remaining time, which
				// also keeps it correct before the client has learned our clock at all - it learns that only from a
				// DispatchPong, and world entry can precede the first LatencyCheck.
				await SendMagicBuffIir(client, self, self, buff.BuffId, MagicBuffDatas.ApplyNow(spellBase, remainingCs, CasterLevel(client)));
				restored++;
			}

			if (restored > 0)
				Logger.LogInformation("Restored {Count} persisted buff(s) for {Name}.", restored, character.Name);
		}

		// The single per-session energy loop: drains while sprinting, REGENS when idle, and steps aside while weaving
		// (the weave owns the drain + the 0x200E stream). Persistent - it keeps ticking for the whole session so energy
		// recovers after a weave/sprint - and exits only on disconnect. A PeriodicTimer loop, not a self-rescheduling
		// callback. EnergyLoopActive guards against a second loop.
		//
		// It also MIRRORS the pool to the client as CharacterStatId.Energy, which is not a decoration: 1000028 is the
		// value the client's own energy model holds (FUN_0112ad70 caches it and drives the Exhausted state off it), and
		// it is what the sprint bar's view is seeded from. Retail streams exactly this - 1,650 records across the
		// corpus, on StatUpdateIIR (0x96B8DC59), per entity - and its cadence is a push every ~3-7s WHILE the pool is
		// moving, going silent once it is back at the max (Hoxxling_..._gameplay2: 14000 -> 13977 -> 14000 -> quiet,
		// with ~4800-point drops for real sprints). EnergyMirrorTicks + the `!= mirrored` guard reproduce that shape:
		// the loop is silent at full and speaks only while the number is changing.
		//
		// ⚠️ This is a MIRROR, not the visual. The client sprints and drains locally with no server traffic at all -
		// which is why retail sends no 0x200E during a sprint - so what this restores is the server's number, not the
		// bar. Only the SPELLWEAVE drain is server-driven, and that owns the 0x200E stream (see SpellweaveService).
		private async Task RunEnergyMirror(INetworkClient client)
		{
			client.Session.EnergyLoopActive = true;
			try
			{
				// What was last put on the wire as CharacterStatId.Energy, so the mirror only speaks when the pool
				// actually moved. uint.MaxValue is "nothing sent yet" rather than 0, which is a legal energy value.
				var mirrored = uint.MaxValue;
				var sinceMirror = 0;
				var exhausted = false;

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
					else if (session.Energy < session.EnergyMax)
						session.Energy = Math.Min(session.EnergyMax, session.Energy + EnergyRegenPerTick);

					// Exhausted rides the pool, not the weave: retail applies it the instant Energy reaches 0 and sends
					// the end twin on the first push that lifts it back above zero, sprint included
					// (SpellweaveCatalog.ExhaustedBuff carries the capture). The weave has its own copy of this because
					// it also owns a health DoT while empty; this is the plain-sprint half.
					if (session.Character is { } self)
					{
						if (session.Energy == 0 && !exhausted)
						{
							exhausted = true;
							await ApplyBuffById(client, self.Id, self.Id, SpellweaveCatalog.ExhaustedBuff);
						}
						else if (session.Energy > 0 && exhausted)
						{
							exhausted = false;
							await SendBuffRemoval(client, self.Id, self.Id, SpellweaveCatalog.ExhaustedBuff);
						}
					}

					if (++sinceMirror >= EnergyMirrorTicks && session.Energy != mirrored && session.Character != null)
					{
						sinceMirror = 0;
						mirrored = session.Energy;
						await SendStatUpdate(client, new CharacterStatBlock { [CharacterStatId.Energy] = session.Energy },
							session.Character.Id);
					}
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
		// The trailing 1 is the client's FORM byte (0 = apply, 1 = end, 2 = update), not a rank - see #438 and
		// docs/subsystems/npc-spell-applyspell.md.
		private async Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			// ⚠️ **DEREGISTER FIRST - "the buff ended" is a state change, not just a packet** (#690). This used to send
			// the twin and leave the live `ActiveBuffs` entry standing, and each caller was expected to remove it
			// itself. Two of five did. The spellweave trance is where it surfaced: cancelling a weave told the CLIENT
			// the trance buff was over, the server kept it, the autosave persisted it (it is a self-buff no dedicated
			// re-applier owns, so `ShouldPersistBuff` says yes), and the next login restored it - leaving the player
			// with the trance's effects and sound looping around them, no weave behind it and nothing to cancel.
			//
			// Removing it here makes every path agree, and is a no-op for the two that already did it (the tick's
			// expiry sweep takes the buff out of the dictionary before it sends, `CancelBuff` needs the entry first to
			// read its caster).
			//
			// ⚠️ Only when the removal is ABOUT the player. Since #776 this op also ends a debuff on a MOB, and an
			// unconditional deregister would then reach into the player's registry with the mob's spell id and drop a
			// buff of theirs that happens to share it. The world tick removes the mob's entry itself, the same way the
			// player expiry sweep does.
			if (_simulation.PlayerWorldEntity(client) is { } self && self.Handle == applyTo)
				self.ActiveBuffs.TryRemove(appliedSpellId, out _);

			var endId = (appliedSpellId & 0xFFFFFF00u) | 0x01u; // form 1 = the "end" twin of the applied buff id
			var body = MagicBuffCodec.Build(applyTo, applyFrom, endId, []);

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, b => b.WriteArrayUInt32Length(body)));

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
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchLearnSpell,
				LearnSpellCodec.Build(spellId)));
		}

		// Sends the MagicBuffIIR (ApplySpell 0x6B87E316) carrier with an already-CONSTRUCTED payload and, for a SELF
		// buff (applyTo == applyFrom), registers it so the server tracks it (.buffs / CancelBuff / tick expiry). The
		// duration is read back from the payload (+8, centiseconds): a known duration (> 0) arms the tick to expire it
		// and send the retail removal twin; 0 = no self-duration (lives until cancelled). Every ApplySpell send path
		// funnels through here - the self-buff, the spellweave trance/component buffs, the mount ride buff and the
		// BCC cast (#69/#274). Offensive casts (applyTo != applyFrom) are damage, handled by ResolveOffensiveCast.
		private async Task SendMagicBuffIir(INetworkClient client, uint applyTo, uint applyFrom, uint spellId, MagicBuffData payload)
		{
			// RE-APPLY = REPLACE, not stack. The client accumulates one icon per ApplySpell it receives (#274 saw this
			// with an NPC debuff re-cast every swing: copies piling up at 0s with no countdown), but our registry is a
			// dictionary keyed by the wire spell id, so N applications of the SAME spell collapse to ONE entry. The tick
			// then expires that one entry and sends ONE end-twin, and every other icon of that spell is orphaned - it
			// counts to 0 and sticks there forever, with nothing left server-side that could ever remove it. That is the
			// "some of the buffs got removed, others didn't - of the same spell" report.
			//
			// Rather than teach the registry to hold N stacks of one id (which would also need the end-twin to identify
			// WHICH stack, and the twin carries an empty payload, so it cannot), keep the invariant the registry already
			// assumes: at most one live instance per spell id. Clearing the old icon first means a refresh looks like a
			// refresh - one icon with a restarted countdown - and there is never an orphan to strand. Retail's own NPC
			// debuff cadence agrees: it re-applies only AFTER the previous one has ended (#274).
			if (applyTo == applyFrom && _simulation.PlayerWorldEntity(client) is { } live && live.Handle == applyTo && live.ActiveBuffs.TryRemove(spellId, out var previous))
			{
				await SendBuffRemoval(client, applyTo, previous.CasterHandle, spellId);
				Logger.LogDebug("Buff 0x{Buff:X8} re-applied to {Target:X8} - cleared the previous instance first so the client shows one icon, not a stack.", spellId, applyTo);
			}

			// The ApplySpell carrier body - the SAME layout for player and NPC casts (NpcSpellCastCodecTests). The
			// codec composes the data region from the VALUE (#539); built once so the identical bytes go to the caster
			// AND to nearby viewers below.
			var body = MagicBuffCodec.Build(applyTo, applyFrom, spellId, payload);

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(body)));

			// #48-analogue: nearby players see the cast EFFECT, not just the gesture. Retail streams the actor's
			// ApplySpell to observers who see the caster (other_playersvisible capture: other-player self-buffs +
			// offensive casts, framed to our endpoint). Reframe the same body to each viewer who already sees the caster.
			await _broadcaster.BroadcastSpellToViewers(client, body);

			if (applyTo == applyFrom)
			{
				// #371: only a TIMED self-buff is tracked. A zero-duration spell is an INSTANT effect (the RDB leads:
				// field 0x008 absent == "no duration"), not a permanent buff - it has nothing for TickBuffExpiry to
				// expire, renders no icon, and registering it would leak a never-expiring ActiveBuffs entry that also
				// persists (ExpiresAt NULL) and re-applies on every login.
				// ⚠️ This used to add "the wire ApplySpell is still SENT above (retail's carrier)". That was never
				// measured and is FALSE: retail sends no ApplySpell for a zero-duration spell (7 of 5,423 in the
				// corpus, all of them buffs whose duration we are missing), and sending one aborts the client's
				// ActivateBuff and eats the spell's recast (#767). `ApplySpell` now returns before the send, so a
				// zero duration reaches here only from the paths that build a payload directly.
				// We just don't TRACK an instant as an active buff. Every real buff has a finite
				// duration (mount ride 24h, Shield 1h/10h, longest is Barrel Blast 1000h), so nothing legitimate needs
				// the old "lives until cancelled" (null) path. This mirrors the NPC-debuff path (WorldSimulation), which
				// already tracks only when duration > 0.
				var durationMs = ActiveBuff.DurationMsFromPayload(payload);
				if (durationMs == 0)
				{
					// An UNTRACKED apply: nothing will ever expire this id, so if the client DOES render an icon for it
					// the icon sticks at 0s forever. #371 concluded a zero-duration spell is an instant that renders no
					// icon - if a stuck icon is ever seen, this line names the culprit and disproves that. It is the
					// first thing to check for "an effect reached 0s and was not removed": the payload's duration comes
					// from Spell.DurationMs (RDB field 0x008) and 9217 of our 20848 spell rows carry none, 69 of them
					// while still carrying stat effects.
					Logger.LogDebug("Buff 0x{Buff:X8} applied to {Target:X8} with NO duration - not tracked, so it will never be expired or removed.", spellId, applyTo);
				}
				else
				{
					var entity = _simulation.PlayerWorldEntity(client);
					if (entity != null && entity.Handle == applyTo)
					{
						entity.ActiveBuffs[spellId] = new ActiveBuff(spellId, applyFrom, DateTimeOffset.UtcNow, durationMs / 1000f);
						// #367: the buff may carry stat deltas (armor/ratings/attributes) - fold them into the live stats.
						await SyncBuffStats(client);
					}
				}
			}
		}

		// Re-derives the player's buffed stats (#367) after ANY change to their live buffs - applied, expired, cancelled,
		// broken, cleared on death - and pushes the ones that moved. Called at every ActiveBuffs mutation rather than
		// folded into the apply path, because a stat a buff raised has to come back DOWN when it ends, and the end
		// happens in four different places (tick expiry, .cancelbuff, shield break, death).
		//
		// Cheap and quiet by design: most buffs carry no honored stat delta, so Sync returns nothing and no packet is
		// sent. Only a real change reaches the wire.
		/// <summary>
		///     Re-reads the character's equipped gear into the live entity's <see cref="WorldEntity.GearBonuses" /> and
		///     re-syncs the stats that derive from it. Call after any change to what is WORN.
		/// </summary>
		/// <remarks>
		///     <c>GearBonuses</c> is a CACHE, written once by <c>RegisterPlayerInWorld</c> at world entry, and the buff
		///     recompute reads `gear + buffs` off it - so without this an equip changed nothing until relog, and a buff
		///     applied afterwards would re-assert the stale gear half on top. Same family as #450's stale max: a value
		///     computed once at entry and never refreshed.
		///     <para>
		///         The refresh goes through <c>SetMaxFromBonus</c> (via the buff sync), not a reseed, so the semantics
		///         are right for gear: a bigger max leaves current where it is and fills by regen, a smaller one clamps
		///         current down. Equipping a +HP ring must not heal you, which is why this does NOT use
		///         <c>CombatService.ReseedFromMaxStats</c> - that one is for a level change, where full pools are correct.
		///     </para>
		///     <para>
		///         <b>It pushes NOTHING.</b> Retail's post-equip <c>0x96B8DC59</c> does carry the current pools
		///         (<c>0x001B</c>/<c>0x01F8</c>/<c>0x01FA</c>, never a max - a level-up is the contrary case with all
		///         six), but our pool VALUES are wrong at the source, so emitting them made the HUD render a mana bar
		///         an Assassin should not have. Detail at the call site and in #473; the recompute is the whole job
		///         here until the values are right.
		///     </para>
		///     <para>
		///         ⚠️ Wired into equip and unequip. NOT yet wired into the WEAPON-SET swap (#400,
		///         <c>WorldBroadcaster</c>), which changes <c>ActiveWeaponSet</c> - and
		///         <c>GetEquippedStatItems(characterId, set)</c> is set-dependent, so swapping sets changes the gear
		///         aggregate just as equipping does. It needs a seam from the broadcaster back to here; see Gitea #3.
		///     </para>
		/// </remarks>
		private async Task RefreshGearBonuses(INetworkClient client)
		{
			if (_simulation.PlayerWorldEntity(client) is not { } entity)
				return; // not in the world yet - world entry will build the aggregate itself

			var character = client.Session.Character;
			entity.GearBonuses = MaxStatAggregator.Combine(
				MaxStatAggregator.AggregateGearBonuses(await Database.GetEquippedStatItems(character.Id, character.ActiveWeaponSet)),
				MaxStatAggregator.AggregateFeatBonuses(await Database.GetCharacterFeatEffects(character.Id), character.Level));

			// Recompute the derived live fields off the new aggregate - max pools (which clamps current when a max
			// shrinks), magic damage, the regen rates. The returned change list is DELIBERATELY DISCARDED: it is what
			// a BUFF change would push, and retail's post-equip update carries none of it.
			await _buffStats.Sync(entity);

			// ⚠️ NOTHING IS PUSHED TO THE CLIENT HERE, on purpose - the internal recompute above is the whole job.
			//
			// Retail DOES send the current pools after an equip (0x001B / 0x01F8 / 0x01FA, never a max - the client
			// derives the max from the item itself). We do not, yet, because the values we would send are wrong at the
			// source: our current mana for a geared Assassin is 0 where retail's is 74, because the snapshot takes it
			// from the persisted CharacterMaxStats instead of the live geared vitals. Pushing `ManaCurrent = 0`
			// against the client's own max of 74 made the HUD draw a mana bar the class does not show - retail has
			// that same 74 mana and NO bar, because its pool is FULL rather than empty (retail sheet, AssassinL23:
			// `Mana 74 / 74`, no HUD bar).
			//
			// So the push waits on the pool VALUES being right, which is #450's "is our base correct" question rather
			// than anything about equipping. Combat, regen and the bar-fill clamp all read the recomputed values above
			// and are unaffected. See #473.
		}

		private async Task SyncBuffStats(INetworkClient client)
		{
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
				return;

			var changed = await _buffStats.Sync(entity);
			if (changed.Count == 0)
				return;

			var block = new CharacterStatBlock();
			foreach (var (stat, value) in changed)
				block.Set(stat, value);

			await SendStatUpdate(client, block, entity.Handle);
			Logger.LogInformation("Buff stats for {Name}: {Stats}.", client.Session.Character?.Name, string.Join(", ", changed.Select(c => $"{c.Stat}={c.Value}")));
		}

		private async Task SendShieldBuff(INetworkClient client, uint self, uint spellBase)
		{
			var spell = await Database.GetSpell(spellBase);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			await SendMagicBuffIir(client, self, self, spellBase << 8, MagicBuffDatas.ApplyNow(spellBase, durationCs, CasterLevel(client)));
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
		// (retail's form-1 end twin) before applying the new one so only one is ever shown.
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
		// -> GameCharInterfaceProject/instance; energy full = 9000 for the necro, 14000 for the melee classes).
		private static Task SendSprintBar(INetworkClient client, uint energy)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x54), client.Endpoint(RpcInterfaces.GameCharInterfaceProjectType),
				GameCharInterfaceProjectMethods.DispatchUpdateSprintBar, b => b.WriteUInt32(energy).WriteByte(0x01)));
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
			// The spellweave trance is SESSION state and is excluded outright (#690) - see SpellweaveCatalog.IsTranceBuff.
			return buff.CasterHandle == entity.Handle && !DedicatedRestoreBuffIds.Contains(buff.BuffId) && !SpellweaveCatalog.IsTranceBuff(buff.BuffId);
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantability / future
		// level-up), false for the silent per-login re-grant.
		// `sender` defaults to the login/world grant sender (GameCharAgent 0x58); the create-editor world-prep grant
		// rides its own conversation (GameCharAgent 0x0C), so it passes that in - the only thing that differs between
		// the two grant sites (the 81-byte body incl. the TEA key comes wholly from the Ability row either way).
		internal static Task SendAddSpecialAbility(INetworkClient client, Ability ability, bool announce = true, ProtoEndpoint? sender = null)
		{
			return client.SendAsync(new Rpc(sender ?? LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddSpecialAbility,
				AddSpecialAbilityCodec.Build(ability, announce)));
		}

		// Emits the level-up attribute feedback: one AttributeChanged/Increased on-screen line per primary attribute
		// that rose (#209). This is the VISIBLE half of a level-up; the caller still pushes the new vitals maxima.
		// `levelsGained` covers a multi-level jump (e.g. a `.level 40` set). The growth amounts are provisional
		// (Combat.AttributeGrowth) until real per-class tables exist.
		private static async Task SendLevelUpFeedback(INetworkClient client, uint newLevel, uint levelsGained = 1)
		{
			var character = client.Session.Character;
			foreach (var (attributeId, amount) in AttributeGrowth.ForLevelGain(character.Class, newLevel, levelsGained))
				await SendFeedback(client, new AttributeChanged(attributeId, amount));
		}
		// --- ISpellweaveOutbound: the spellweaving FSM's wire + world-state surface. Explicit forwards to the
		// methods that already existed, which is all the seven Func<> seams ever were - see ISpellweaveOutbound.
		bool ISpellweaveOutbound.IsMounted(INetworkClient client)
		{
			return _simulation.PlayerWorldEntity(client)?.IsMounted == true;
		}

		void ISpellweaveOutbound.EnsureEnergyLoop(INetworkClient client)
		{
			EnsureEnergyLoop(client);
		}

		Task ISpellweaveOutbound.ApplyBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId)
		{
			return ApplyBuffById(client, applyTo, applyFrom, wireSpellId);
		}

		Task ISpellweaveOutbound.RemoveBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId)
		{
			return SendBuffRemoval(client, applyTo, applyFrom, wireSpellId);
		}

		Task ISpellweaveOutbound.SendFeedback(INetworkClient client, string message)
		{
			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, message);
		}

		Task ISpellweaveOutbound.SendMountedRefusal(INetworkClient client)
		{
			return SendWorldChat(client, ChatTextXml.MustNotHaveMountRunning);
		}

		Task ISpellweaveOutbound.SendSprintBar(INetworkClient client, uint energy)
		{
			return SendSprintBar(client, energy);
		}
	}
}
