using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The BUFF ENGINE: what happens when a spell lands on someone, for as long as it lasts, and when it ends.
	// Applying (the MagicBuffIIR carrier + its chain + its instant pool move), tracking (the live ActiveBuffs
	// registry the world tick expires), removing (retail's form-1 end twin), restoring across a relog, and folding a
	// buff's stat deltas into the live entity.
	//
	// It used to be `GameServerListener.Buffs.cs`, 848 lines of it, and the cost was visible in the shape of the code
	// around it: THREE separate ports had been invented to reach it from services that could not depend on the
	// listener - `IWorldOutbound.ApplySelfBuff`/`SendBuffRemoval`/`SyncBuffStats` (the world tick),
	// `ISpellEffectOutbound.ApplySelfBuff` (#439's data-driven effects) and `ISpellweaveOutbound.ApplyBuff`/
	// `RemoveBuff` (the weave FSM). Those ports still exist, because they carry other ops too, but their buff members
	// are now one-line forwards to this class rather than the only way to reach the implementation.
	//
	// What deliberately stayed on the listener: the ENERGY/sprint loop (a resource pool, not a buff - it merely
	// applies the Exhausted buff through here), the pet summons, and the spell/ability GRANT packets.
	public sealed class BuffService
	{
		// Shield of the Risen (#221) base spell ids - the two persistent protection buffs applied on every world entry.
		private const uint ShieldOfTheRisenBase1 = 4061768; // 1h  (wire (base<<8) = 0x3DFA4800)
		private const uint ShieldOfTheRisenBase2 = 3666828; // 10h (wire (base<<8) = 0x37F38C00)

		// The buff ids owned by a dedicated per-entry re-applier - Shield of the Risen (SendShieldOfTheRisen) and the
		// mount ride buff (RestoreMount). These are re-asserted on EVERY world entry from that flush, so the generic
		// buff persistence must skip them or the client would render a duplicate icon on relog (#274/#221).
		private static readonly HashSet<uint> DedicatedRestoreBuffIds = [ShieldOfTheRisenBase1 << 8, ShieldOfTheRisenBase2 << 8, MountCatalog.RideSpell];

		private readonly WorldBroadcaster _broadcaster;

		// #367: the lasting stat deltas a buff carries, re-derived at every registry mutation.
		private readonly BuffStatEffects _buffStats;

		// The player's live vitals, for the INSTANT pool move a landing spell carries (#702). The world model owns
		// them; CombatService is their accessor.
		private readonly CombatService _combat;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// #702: the instant pool moves (heal / mana / stamina) a spell's rows state.
		private readonly SpellPoolEffects _poolEffects;

		// #784: the `CastNano` chain edges - "when this spell lands, ALSO run X".
		private readonly SpellAreaCatalog _spellAreas;
		private readonly WorldSimulation _sim;

		public BuffService(IDatabaseRepository database, WorldSimulation sim, WorldBroadcaster broadcaster, CombatService combat,
			SpellAreaCatalog spellAreas, SpellPoolEffects poolEffects, BuffStatEffects buffStats, ILogger logger)
		{
			_database = database;
			_sim = sim;
			_broadcaster = broadcaster;
			_combat = combat;
			_spellAreas = spellAreas;
			_poolEffects = poolEffects;
			_buffStats = buffStats;
			_logger = logger;
		}

		// The level that fills a buff payload's @26/@42 words (SelfBuffCodec): the CASTER's, which for every path here
		// is the player. Was a hardcoded 80 - right only for a level-80 character (#438).
		public static uint CasterLevel(INetworkClient client)
		{
			return client.Session.Character?.Level ?? 1;
		}

		// Whether PlayerPersistenceService should persist a live buff for relog (#274): only a SELF-buff (caster is the
		// entity itself) that no dedicated re-applier already owns. NPC-cast debuffs carry a session-volatile caster
		// handle plus a DoT and deliberately do not resume across a relog. Pure predicate - the persistence service
		// (World layer) calls it without otherwise reaching into the buff engine.
		public static bool ShouldPersistBuff(WorldEntity entity, ActiveBuff buff)
		{
			// The spellweave trance is SESSION state and is excluded outright (#690) - see SpellweaveCatalog.IsTranceBuff.
			return buff.CasterHandle == entity.Handle && !DedicatedRestoreBuffIds.Contains(buff.BuffId) && !SpellweaveCatalog.IsTranceBuff(buff.BuffId);
		}

		// Whether a buff's CASTER is a player character - `MagicBuffData`'s `bool2` and nothing else (#658). Answered
		// from the world model rather than from the handle's shape: retail's split is by what the caster IS, and a
		// handle-range test would be a coincidence that happens to hold on the corpus's own id allocation.
		// An unknown handle is not a player, which is also what retail sends when there is no caster at all.
		public bool CasterIsPlayer(INetworkClient client, uint casterHandle)
		{
			var player = _sim.PlayerWorldEntity(client);

			return player != null && (player.Handle == casterHandle
				|| (_sim.World.ZoneForEntity(player).TryGet(casterHandle, out var caster) && caster.IsPlayer));
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
		//
		// `chain` is the #784 `CastNano` walk in progress, and is null at every ORIGINAL landing - a cast, a self-buff,
		// a spell effect. It is non-null only when this call IS a chain edge, which is what stops the walk from
		// re-entering a spell it has already applied to this subject.
		public async Task ApplySpell(INetworkClient client, uint applyTo, uint applyFrom, uint spellId, SpellChainWalk? chain = null)
		{
			var spell = await _database.GetSpell(spellId) ?? await _database.GetSpell(spellId >> 8);

			// Seed the walk with the record that is landing here, so an edge pointing back at it terminates.
			chain ??= new SpellChainWalk();
			chain.TryEnter(spell?.Id ?? spellId >> 8);

			// A cast spell can carry no effect of its own and name a SEPARATE record that is the actual buff - the
			// castable is the trigger, the EffectSpell is what lands. Frozen Hatred is the proven case (#434): the
			// necro casts 4377207, whose row has no duration at all, and retail answers with an ApplySpell for
			// 4377209 - a distinct id, same name, carrying the 27.7h duration and the cold-effect resource
			// (xxx_mage-necromancer beach->city, cast fr5393 -> ApplySpell 0x42CA7900 fr5400). Applying the CAST id
			// meant a zero-duration record that renders no icon and tracks no buff, i.e. casting it did nothing.
			if (spell is { DurationMs: 0, EffectSpell: > 0 } && await _database.GetSpell(spell.EffectSpell) is { } effect)
			{
				var effectWireId = effect.Id << 8;
				_logger.LogInformation("Cast {Cast} ('{Name}') carries no effect of its own; applying its effect spell {Effect} ('{EffectName}').", spell.Id, spell.Name, effect.Id, effect.Name);
				await SendMagicBuffIir(client, applyTo, applyFrom, effectWireId, MagicBuffDatas.ApplyNow(effect.Id, effect.DurationMs / 10, CasterLevel(client), CasterIsPlayer(client, applyFrom)));
				await ApplyPoolEffects(client, applyTo, effect.Id);

				// BOTH records' chain edges (#784). The carrier is what was cast and the effect is what landed, they are
				// two records with two element lists, and 232 carriers of this shape state edges of their own. The
				// visited set collapses the 22 that chain straight to their own effect spell, so the union costs
				// nothing where the two overlap.
				await ApplyChainedSpells(client, applyTo, applyFrom, spell.Id, chain);
				if (chain.TryEnter(effect.Id))
					await ApplyChainedSpells(client, applyTo, applyFrom, effect.Id, chain);

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
				_logger.LogDebug("Cast {Spell} has no duration and no effect spell - no ApplySpell sent (retail sends none; a zero-duration one aborts the client's ActivateBuff and eats the spell's recast).", spellId >> 8);
				await ApplyPoolEffects(client, applyTo, spellId >> 8); // an INSTANT pool move still lands (#702)

				// ⚠️ The chain still runs. "No ApplySpell" is a statement about the CARRIER's icon, not about whether it
				// landed - an instant nuke that chains a 30 s debuff is the ordinary shape, and returning here would
				// drop exactly the edge #784 is about.
				await ApplyChainedSpells(client, applyTo, applyFrom, spellId >> 8, chain);
				return;
			}

			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, MagicBuffDatas.ApplyNow(spellId >> 8, durationCs, CasterLevel(client), CasterIsPlayer(client, applyFrom)));
			await ApplyPoolEffects(client, applyTo, spellId >> 8);
			await ApplyChainedSpells(client, applyTo, applyFrom, spellId >> 8, chain);
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
		public async Task ApplyPoolEffects(INetworkClient client, uint applyTo, uint spellBase)
		{
			if (applyTo != client.Session.Character.Id)
				return;

			var effects = await _poolEffects.ForSpellBase(spellBase, CasterLevel(client));
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

				var amount = effect.For(max, CasterLevel(client));
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
				_logger.LogInformation("Spell {Spell} moved {Name}'s {Pool} by {Amount}.", spellBase, client.Session.Character.Name, effect.Pool, amount);
			}

			if (moved)
				await SendStatUpdate(client, vitals.ToCurrentsBlock(), client.Session.Character.Id);
		}

		// Applies a spellweave buff (trance buff on activate, or a component's own spell on add) with the CORRECT
		// 54-byte MagicBuffIIR blob (SelfBuffCodec.BuildBuffDataLong) instead of the shared 50-byte self-buff form -
		// the client rejects the short shape for these ("MagicBuffIIR_c::ReadSubClass failed"). Looks up the spell's
		// real duration so the client counts it down and SendMagicBuffIir arms the matching expiry. The narrow seam
		// the SpellweaveService uses for every buff it emits.
		public async Task ApplyBuffById(INetworkClient client, uint applyTo, uint applyFrom, uint spellId)
		{
			var spellBase = spellId >> 8;
			var spell = await _database.GetSpell(spellBase) ?? await _database.GetSpell(spellId);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, MagicBuffDatas.ApplyNow(spellBase, durationCs, CasterLevel(client), CasterIsPlayer(client, applyFrom)));
		}

		// Shield of the Risen also drops the instant the player commits an AGGRESSIVE action - an offensive cast, a
		// ground AoE, or a melee/unarmed swing. Retail's newbie protection breaks on the first attack, not only when
		// its timer (1h/10h, expired by TickBuffExpiry) runs out. Removes the tracked shield's icon with the rank-1
		// end twin (the client does NOT self-remove it, same as .cancelbuff), drops it from the live registry, and
		// forgets the session tracking so the next playfield entry re-applies cleanly. No-op when no shield is active
		// (so a plain self-buff cast, which never sets ActiveShieldSpellBase, leaves it untouched).
		public async Task BreakShieldOnAggression(INetworkClient client)
		{
			if (client.Session.ActiveShieldSpellBase is not { } shieldBase)
				return;

			var self = client.Session.Character.Id;
			var appliedId = shieldBase << 8;

			// Retail shows the "lost the grace" on-screen line the instant the shield is LOST to an aggressive action,
			// sent right BEFORE the buff-removal twin (conans-castle-exit rec#508). Without it the icon vanishes with no
			// notification. Only fired here (the aggression loss), not on a playfield swap (SendShieldOfTheRisen).
			await WorldIir.Send(client, FormatFeedbackCodec.Build(self, new FeedbackLiteral(FeedbackXml.LostGraceEffect, FormatFeedbackCodec.EffectLineTypeFlags)));
			await SendBuffRemoval(client, self, self, appliedId); // deregisters the live buff too

			await SyncBuffStats(client);
			client.Session.ActiveShieldSpellBase = null;
			_logger.LogInformation("Shield of the Risen broken by aggressive action for {Name}.", client.Session.Character?.Name);
		}

		// Re-applies a player's persisted self-buffs at world entry with their REMAINING time (#274). Reads the
		// CharacterBuff snapshot taken at the last logout/autosave, drops any that lapsed while offline, rebuilds each
		// ApplySpell payload from the persisted base id / effect nonce with a duration recomputed
		// to the time left, and sends it through SendMagicBuffIir - which re-registers the live ActiveBuff so the world
		// tick expires it on schedule and the client counts down the correct remaining time. Runs in the post-0x205F
		// flush beside RestoreMount / SendShieldOfTheRisen, which own their own buffs (excluded at save), so nothing is
		// double-applied. A permanent buff (ExpiresAt == null) restores with a 0 duration (lives until cancelled).
		public async Task RestoreBuffs(INetworkClient client)
		{
			var character = client.Session.Character;

			var buffs = await _database.GetCharacterBuffs(character.Id);
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
				await SendMagicBuffIir(client, self, self, buff.BuffId, MagicBuffDatas.ApplyNow(spellBase, remainingCs, CasterLevel(client), casterIsPlayer: true));
				restored++;
			}

			if (restored > 0)
				_logger.LogInformation("Restored {Count} persisted buff(s) for {Name}.", restored, character.Name);
		}

		// The server->client buff REMOVAL packet (#253/#274), decoded from the mount-dismount captures (mount buff end
		// 0x31eb1501 in *_dismount) and the combat captures (NPC-debuff ends 0x31644901 with applyTo=player,
		// applyFrom=NPC): the SAME ApplySpell (0x6B87E316) carrier as the apply, but with the spell id's rank byte set
		// to 1 (the "end" twin) and an EMPTY effect payload. The client clears the buff icon on receipt - so a
		// SERVER-initiated cancel (.cancelbuff), a dispel, or the tick-driven expiry removes the icon live instead of
		// only on relog. applyTo/applyFrom MIRROR the apply: equal for a self-buff, applyTo=player + applyFrom=NPC for
		// an NPC-cast debuff. Byte-exact carrier content (after the IIR [u32 len=0x1A]):
		//   [ApplySpell][c350][applyTo][u16 0][(base<<8)|1][c350][applyFrom]   (no payload)
		// The trailing 1 is the client's FORM byte (0 = apply, 1 = end, 2 = update), not a rank - see #438 and
		// docs/subsystems/npc-spell-applyspell.md.
		public async Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
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
			if (_sim.PlayerWorldEntity(client) is { } self && self.Handle == applyTo)
				self.ActiveBuffs.TryRemove(appliedSpellId, out _);

			var endId = (appliedSpellId & 0xFFFFFF00u) | 0x01u; // form 1 = the "end" twin of the applied buff id
			var body = MagicBuffCodec.Build(applyTo, applyFrom, endId, []);

			await WorldIir.Send(client, body);

			// The removal goes to VIEWERS too, for the same reason the application does (SendMagicBuffIir relays it):
			// a buff the other clients were told to start must be told to stop, or their copy of it never ends. The
			// spellweave trance is where this showed: viewers saw the caster enter the weave pose (relayed apply) and
			// then hold it forever, because the fire/cancel removal only ever reached the caster. It is not weave-
			// specific - EVERY buff end was stale on other screens, the weave just made it obvious by being a pose
			// rather than an icon.
			await _broadcaster.BroadcastSpellToViewers(client, body);
		}

		// Sends the MagicBuffIIR (ApplySpell 0x6B87E316) carrier with an already-CONSTRUCTED payload and, for a SELF
		// buff (applyTo == applyFrom), registers it so the server tracks it (.buffs / CancelBuff / tick expiry). The
		// duration is read back from the payload (+8, centiseconds): a known duration (> 0) arms the tick to expire it
		// and send the retail removal twin; 0 = no self-duration (lives until cancelled). Every ApplySpell send path
		// funnels through here - the self-buff, the spellweave trance/component buffs, the mount ride buff and the
		// BCC cast (#69/#274). Offensive casts (applyTo != applyFrom) are damage, handled by ResolveOffensiveCast.
		public async Task SendMagicBuffIir(INetworkClient client, uint applyTo, uint applyFrom, uint spellId, MagicBuffData payload)
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
			if (applyTo == applyFrom && _sim.PlayerWorldEntity(client) is { } live && live.Handle == applyTo && live.ActiveBuffs.TryRemove(spellId, out var previous))
			{
				await SendBuffRemoval(client, applyTo, previous.CasterHandle, spellId);
				_logger.LogDebug("Buff 0x{Buff:X8} re-applied to {Target:X8} - cleared the previous instance first so the client shows one icon, not a stack.", spellId, applyTo);
			}

			// The ApplySpell carrier body - the SAME layout for player and NPC casts (NpcSpellCastCodecTests). The
			// codec composes the data region from the VALUE (#539); built once so the identical bytes go to the caster
			// AND to nearby viewers below.
			var body = MagicBuffCodec.Build(applyTo, applyFrom, spellId, payload);

			await WorldIir.Send(client, body);

			// #48-analogue: nearby players see the cast EFFECT, not just the gesture. Retail streams the actor's
			// ApplySpell to observers who see the caster (other_playersvisible capture: other-player self-buffs +
			// offensive casts, framed to our endpoint). Reframe the same body to each viewer who already sees the caster.
			await _broadcaster.BroadcastSpellToViewers(client, body);

			if (applyTo != applyFrom)
				return;

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
				_logger.LogDebug("Buff 0x{Buff:X8} applied to {Target:X8} with NO duration - not tracked, so it will never be expired or removed.", spellId, applyTo);
				return;
			}

			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null || entity.Handle != applyTo)
				return;

			entity.ActiveBuffs[spellId] = new ActiveBuff(spellId, applyFrom, DateTimeOffset.UtcNow, durationMs / 1000f);
			// #367: the buff may carry stat deltas (armor/ratings/attributes) - fold them into the live stats.
			await SyncBuffStats(client);
		}

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
		public async Task RefreshGearBonuses(INetworkClient client)
		{
			if (_sim.PlayerWorldEntity(client) is not { } entity)
				return; // not in the world yet - world entry will build the aggregate itself

			var character = client.Session.Character;
			entity.GearBonuses = await CharacterStatBonuses.For(_database, character);

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

		// Re-derives the player's buffed stats (#367) after ANY change to their live buffs - applied, expired, cancelled,
		// broken, cleared on death - and pushes the ones that moved. Called at every ActiveBuffs mutation rather than
		// folded into the apply path, because a stat a buff raised has to come back DOWN when it ends, and the end
		// happens in four different places (tick expiry, .cancelbuff, shield break, death).
		//
		// Cheap and quiet by design: most buffs carry no honored stat delta, so Sync returns nothing and no packet is
		// sent. Only a real change reaches the wire.
		public async Task SyncBuffStats(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			var changed = await _buffStats.Sync(entity);
			if (changed.Count == 0)
				return;

			var block = new CharacterStatBlock();
			foreach (var (stat, value) in changed)
				block.Set(stat, value);

			await SendStatUpdate(client, block, entity.Handle);
			_logger.LogInformation("Buff stats for {Name}: {Stats}.", client.Session.Character?.Name, string.Join(", ", changed.Select(c => $"{c.Stat}={c.Value}")));
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
		public async Task SendShieldOfTheRisen(INetworkClient client)
		{
			var self = client.Session.Character.Id;
			var map = await _database.GetMap(client.Session.Character.MapId);
			var desired = map?.InstanceMode is null or MapInstanceMode.Shared
				? ShieldOfTheRisenBase2 // 10h shared/open-world
				: ShieldOfTheRisenBase1; // 1h instanced

			if (client.Session.ActiveShieldSpellBase is { } prior && prior != desired)
				await SendBuffRemoval(client, self, self, prior << 8);

			await SendShieldBuff(client, self, desired);
			client.Session.ActiveShieldSpellBase = desired;
		}

		// #784: the `CastNano` (53051) chain - "when this spell lands, ALSO run X", on the SAME subject. Distinct from
		// the AREA elements beside it in the same table, which pick their own targets by radius; a chain edge states no
		// radius at all, which is why reading one as an area swept a 0 m circle and reached nobody.
		//
		// This is the mechanic `Gangrenous Stench` was missing its headline half of: 221069 lands on a victim and its
		// chain edge runs 3963058 `Physical Wrack` at the same victim - the -10% Slashing/Piercing/Crushing/Poison the
		// in-client tooltip shows. #783 imported the edge and stopped there; nothing walked it.
		//
		// ⭐ A chain edge's PERIOD is now READ, and #786's two candidate readings both turned out to be right - for
		// different rows. The client names the pair in one log line (`"Fixing broken gameaction data, Duration was 0
		// while Interval was N"`, FUN_0089b390), so 0x2d6 is the INTERVAL and 0x2d5 the DURATION, and the DURATION is
		// a repeat COUNT: -1 unbounded, 1 once, a thin tail of 2..40. Of the 483 edges carrying an interval, 263 state
		// -1 (a cadence) and 220 state 1 (so there the interval is a DELAY).
		//
		// This path still runs every edge ONCE when the carrier lands, which is right for both halves - it is the
		// first run. The cadence half then re-runs from the aura tick (`WorldSimulation.EmitAuraAreas`); the delay
		// half is unwired, because a delayed single run needs a one-shot timer the tick does not have and firing it
		// early is the only alternative.
		private async Task ApplyChainedSpells(INetworkClient client, uint applyTo, uint applyFrom, uint carrierBase, SpellChainWalk chain)
		{
			foreach (var link in await _spellAreas.ChainedSpells(carrierBase))
			{
				// #813: claim the target's STACKING LINE as well as its id. Ten edges into one line is a rank table,
				// not ten buffs, and the line is a mutual-exclusion group - so the first arrival wins and the rest
				// are refused.
				if (!chain.TryEnter(link.EffectSpellId, await _spellAreas.StackingLineOf(link.EffectSpellId)))
					continue;

				_logger.LogDebug("Spell {Carrier} chains {Chained} onto 0x{Target:X8}.", carrierBase, link.EffectSpellId, applyTo);
				await ApplySpell(client, applyTo, applyFrom, link.EffectSpellId << 8, chain);
			}

			if (chain.Truncated)
				_logger.LogWarning("Spell {Carrier}'s chain hit the {Cap}-application cap and was truncated.", carrierBase, SpellChainWalk.MaxApplications);
		}

		private async Task SendShieldBuff(INetworkClient client, uint self, uint spellBase)
		{
			var spell = await _database.GetSpell(spellBase);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			await SendMagicBuffIir(client, self, self, spellBase << 8, MagicBuffDatas.ApplyNow(spellBase, durationCs, CasterLevel(client), casterIsPlayer: true));
		}

		// Pushes a 0x96B8DC59 stat block to the client (shared wire builder on World.VitalsPacket).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return client.SendAsync(VitalsPacket.BuildStatUpdate(client, stats, entityHandle));
		}
	}
}
