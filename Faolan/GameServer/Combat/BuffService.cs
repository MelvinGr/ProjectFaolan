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
using Faolan.Core.Rdb;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The buff engine: what happens when a spell lands on someone, for as long as it lasts, and when it ends.
	/// </summary>
	/// <remarks>
	///     Covers applying (the MagicBuffIIR carrier, its chain and its instant pool move), tracking (the live
	///     ActiveBuffs registry the world tick expires), removing (retail's form-1 end twin), restoring across a relog,
	///     and folding a buff's stat deltas into the live entity. The buff members of
	///     <c>IWorldOutbound</c>, <c>ISpellEffectOutbound</c> and <c>ISpellweaveOutbound</c> are one-line forwards to
	///     this class. The energy and sprint loop is a resource pool rather than a buff and lives elsewhere; it only
	///     applies the Exhausted buff through here.
	/// </remarks>
	public sealed class BuffService
	{
		// Shield of the Risen base spell ids - the two persistent protection buffs applied on every world entry.
		private const uint ShieldOfTheRisenBase1 = 4061768; // 1h  (wire (base<<8) = 0x3DFA4800)
		private const uint ShieldOfTheRisenBase2 = 3666828; // 10h (wire (base<<8) = 0x37F38C00)

		// The buff ids owned by a dedicated per-entry re-applier - Shield of the Risen (SendShieldOfTheRisen) and the
		// mount ride buff (RestoreMount). These are re-asserted on every world entry from that flush, so the generic buff
		// persistence must skip them or the client renders a duplicate icon on relog.
		private static readonly HashSet<uint> DedicatedRestoreBuffIds = [ShieldOfTheRisenBase1 << 8, ShieldOfTheRisenBase2 << 8, MountCatalog.RideSpell];

		private readonly WorldBroadcaster _broadcaster;

		// The lasting stat deltas a buff carries, re-derived at every registry mutation.
		private readonly BuffStatEffects _buffStats;

		// The player's live vitals, for the instant pool move a landing spell carries. The world model owns them.
		private readonly ICatalogRepository _catalog;
		private readonly ICharacterStatRepository _stats;
		private readonly IWorldRepository _world;
		private readonly ILogger _logger;

		// The instant pool moves (heal, mana, stamina) a spell's rows state.
		private readonly SpellPoolEffects _poolEffects;

		// The `CastNano` chain edges - "when this spell lands, also run X".
		private readonly SpellAreaCatalog _spellAreas;
		private readonly WorldSimulation _sim;

		public BuffService(ICatalogRepository catalog, ICharacterStatRepository stats, IWorldRepository world, WorldSimulation sim, WorldBroadcaster broadcaster,
			SpellAreaCatalog spellAreas, SpellPoolEffects poolEffects, BuffStatEffects buffStats, ILogger logger)
		{
			_catalog = catalog;
			_stats = stats;
			_world = world;
			_sim = sim;
			_broadcaster = broadcaster;
			_spellAreas = spellAreas;
			_poolEffects = poolEffects;
			_buffStats = buffStats;
			_logger = logger;
		}

		/// <summary>
		///     The level that fills a buff payload's @26 and @42 words (SelfBuffCodec): the caster's, which for every path
		///     here is the player.
		/// </summary>
		public static uint CasterLevel(INetworkClient client)
		{
			return client.Session.Character?.Level ?? 1;
		}

		/// <summary>
		///     Whether PlayerPersistenceService should persist a live buff for a relog: only a self-buff, meaning the caster
		///     is the entity itself, that no dedicated re-applier already owns.
		/// </summary>
		/// <remarks>
		///     NPC-cast debuffs carry a session-volatile caster handle plus a damage-over-time and deliberately do not resume
		///     across a relog. A pure predicate, so the persistence service in the World layer can call it without otherwise
		///     reaching into the buff engine.
		/// </remarks>
		public static bool ShouldPersistBuff(WorldEntity entity, ActiveBuff buff)
		{
			// The spellweave trance is session state and is excluded outright - see SpellweaveCatalog.IsTranceBuff.
			return buff.CasterHandle == entity.Handle && !DedicatedRestoreBuffIds.Contains(buff.BuffId) && !SpellweaveCatalog.IsTranceBuff(buff.BuffId);
		}

		/// <summary>
		///     Whether a buff's caster is a player character - <c>MagicBuffData</c>'s <c>bool2</c> and nothing else.
		/// </summary>
		/// <remarks>
		///     Answered from the world model rather than from the handle's shape: retail's split is by what the caster is,
		///     and a handle-range test would be a coincidence that happens to hold on the corpus's own id allocation. An
		///     unknown handle is not a player, which is also what retail sends when there is no caster at all.
		/// </remarks>
		public bool CasterIsPlayer(INetworkClient client, uint casterHandle)
		{
			var player = _sim.PlayerWorldEntity(client);

			return player != null && (player.Handle == casterHandle
				|| (_sim.World.ZoneForEntity(player).TryGet(casterHandle, out var caster) && caster.IsPlayer));
		}

		/// <summary>
		///     Applies a self-buff, or an offensive MagicBuff cast, by constructing its 54-byte MagicBuffIIR payload from
		///     scratch (SelfBuffCodec): the spell's real base id (<c>id &gt;&gt; 8</c>; the low byte is the client's form
		///     selector) plus its RDB duration (<c>Spell.DurationMs</c> as centiseconds at payload+8), so the client counts
		///     down the correct time and the server expires it at the same instant.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The long 54-byte form is the only shape retail ever sends: across all 111 capture dumps the only s2c
		///         MagicBuffIIR tails are 54 bytes (x1938) and 0 (x982, the end twin); a 50-byte tail never occurs. The
		///         client's <c>MagicBuffIIR_c::ReadSubClass</c> runs 4 bytes off the end of a 50-byte payload and rejects
		///         the record ("datasize left in stream=4, raw=76"), so nothing renders.
		///     </para>
		///     <para>
		///         <paramref name="chain" /> is the <c>CastNano</c> walk in progress, and is null at every original landing -
		///         a cast, a self-buff, a spell effect. It is non-null only when this call is itself a chain edge, which is
		///         what stops the walk re-entering a spell it has already applied to this subject.
		///     </para>
		/// </remarks>
		public async Task ApplySpell(INetworkClient client, uint applyTo, uint applyFrom, uint spellId, SpellChainWalk? chain = null)
		{
			var spell = await _catalog.GetSpell(spellId) ?? await _catalog.GetSpell(spellId >> 8);

			// Seed the walk with the record that is landing here, so an edge pointing back at it terminates.
			chain ??= new SpellChainWalk();
			chain.TryEnter(spell?.Id ?? spellId >> 8);

			// A cast spell can carry no effect of its own and name a separate record that is the actual buff - the
			// castable is the trigger, the EffectSpell is what lands. Frozen Hatred is the proven case: the necromancer
			// casts 4377207, whose row has no duration at all, and retail answers with an ApplySpell for 4377209, a
			// distinct id with the same name carrying the 27.7h duration and the cold-effect resource
			// (xxx_mage-necromancer beach->city, cast fr5393 -> ApplySpell 0x42CA7900 fr5400). Applying the cast id
			// would build a zero-duration record that renders no icon and tracks no buff.
			if (spell is { DurationMs: 0, EffectSpell: > 0 } && await _catalog.GetSpell(spell.EffectSpell) is { } effect)
			{
				var effectWireId = effect.Id << 8;
				_logger.LogInformation("Cast {Cast} ('{Name}') carries no effect of its own; applying its effect spell {Effect} ('{EffectName}').", spell.Id, spell.Name, effect.Id, effect.Name);
				await SendMagicBuffIir(client, applyTo, applyFrom, effectWireId, MagicBuffDatas.ApplyNow(effect.Id, effect.DurationMs / 10, CasterLevel(client), CasterIsPlayer(client, applyFrom)));
				await ApplyPoolEffects(client, applyTo, effect.Id);

				// Both records' chain edges. The carrier is what was cast and the effect is what landed; they are two
				// records with two element lists, and 232 carriers of this shape state edges of their own. The visited
				// set collapses the 22 that chain straight to their own effect spell, so the union costs nothing where
				// the two overlap.
				await ApplyChainedSpells(client, applyTo, applyFrom, spell.Id, chain);
				if (chain.TryEnter(effect.Id))
					await ApplyChainedSpells(client, applyTo, applyFrom, effect.Id, chain);

				return;
			}

			var durationCs = (spell?.DurationMs ?? 0) / 10;

			// Retail sends no ApplySpell for a spell with nothing to apply, so the send is gated on there being
			// something to apply. Measured over all 132 dumps: of 5,423 s2c MagicBuffIIR bodies, 5,416 carry a spell
			// with a real duration and seven carry a zero-duration one - and those seven are `Armor of Faith` (x4),
			// `Emerald Aptitude` (x2) and `Resting`, i.e. genuine buffs whose duration is missing on this side, not
			// damage casts. Chill, Ice Strike, Lifestrike and Pestilential Blast - the nukes cast constantly
			// throughout the corpus - never appear once.
			//
			// Sending one anyway breaks the spell's cooldown, in the client's own words (channel GameCode/Magic, at
			// error level):
			//
			//     CoreMagicHolder::ActivateBuff 3666380 remaining time 0
			//     SpellTemplate_t::GetMagicSpell failed to load spell with id 14321      (= 3666380 >> 8)
			//
			// ActivateBuff aborts on a zero remaining time, and the spell's own GameActions - which for `Chill` is
			// exactly one element, `SetSpellLineRecastTime(41028, 400)` - never execute, so the recast arm never
			// happens. The same log shows it working on a spell that does have a duration, arming a whole line at
			// once:
			//
			//     GameCode/Magic/Recast - Setting recast timer on spell: 144674..144677, 1206011 ... to: 600
			if (durationCs == 0)
			{
				_logger.LogDebug("Cast {Spell} has no duration and no effect spell - no ApplySpell sent (retail sends none; a zero-duration one aborts the client's ActivateBuff and eats the spell's recast).", spellId >> 8);
				await ApplyPoolEffects(client, applyTo, spellId >> 8); // an instant pool move still lands

				// The chain still runs. "No ApplySpell" is a statement about the carrier's icon, not about whether the
				// spell landed - an instant nuke that chains a 30 s debuff is the ordinary shape.
				await ApplyChainedSpells(client, applyTo, applyFrom, spellId >> 8, chain);
				return;
			}

			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, MagicBuffDatas.ApplyNow(spellId >> 8, durationCs, CasterLevel(client), CasterIsPlayer(client, applyFrom)));
			await ApplyPoolEffects(client, applyTo, spellId >> 8);
			await ApplyChainedSpells(client, applyTo, applyFrom, spellId >> 8, chain);
		}

		/// <summary>
		///     The instant pool move a landing spell carries - a heal, a mana restore, a stamina refill.
		/// </summary>
		/// <remarks>
		///     <para>
		///         It happens once, when the spell lands, which is why it hangs off <see cref="ApplySpell" /> rather than
		///         <see cref="SendMagicBuffIir" />: these rows sit beside the lasting deltas on the same spell, and the buff
		///         paths that touch a spell repeatedly - the relog restore, the expiry sweep, a re-apply that refreshes a
		///         duration - all go through SendMagicBuffIir and must not carry them.
		///     </para>
		///     <para>
		///         Only a player's own pools: every one of the 52 class-castable carriers is a self-heal or a team-support
		///         spell landing on a player, and an NPC's pools have their own path (ApplyNpcDamage) with a death lifecycle
		///         a heal must not bypass.
		///     </para>
		/// </remarks>
		public async Task ApplyPoolEffects(INetworkClient client, uint applyTo, uint spellBase)
		{
			if (applyTo != client.Session.Character.Id)
				return;

			var effects = await _poolEffects.ForSpellBase(spellBase, CasterLevel(client));
			if (effects.Count == 0)
				return;

			var vitals = _sim.GetOrAddPlayerVitals(client);
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

		/// <summary>
		///     Applies a spellweave buff - the trance buff on activate, or a component's own spell on add - with the
		///     54-byte MagicBuffIIR payload the client requires; it rejects a shorter shape with
		///     "MagicBuffIIR_c::ReadSubClass failed". Looks up the spell's real duration so the client counts it down and
		///     <see cref="SendMagicBuffIir" /> arms the matching expiry. The narrow seam SpellweaveService uses for every
		///     buff it emits.
		/// </summary>
		public async Task ApplyBuffById(INetworkClient client, uint applyTo, uint applyFrom, uint spellId)
		{
			var spellBase = spellId >> 8;
			var spell = await _catalog.GetSpell(spellBase) ?? await _catalog.GetSpell(spellId);
			var durationCs = (spell?.DurationMs ?? 0) / 10;
			await SendMagicBuffIir(client, applyTo, applyFrom, spellId, MagicBuffDatas.ApplyNow(spellBase, durationCs, CasterLevel(client), CasterIsPlayer(client, applyFrom)));
		}

		/// <summary>
		///     Shield of the Risen drops the instant the player commits an aggressive action - an offensive cast, a ground
		///     area effect, or a melee or unarmed swing. Retail's newbie protection breaks on the first attack, not only
		///     when its 1h or 10h timer runs out.
		/// </summary>
		/// <remarks>
		///     Removes the tracked shield's icon with the rank-1 end twin, which the client does not self-remove, drops it
		///     from the live registry, and forgets the session tracking so the next playfield entry re-applies cleanly. A
		///     no-op when no shield is active, so a plain self-buff cast - which never sets ActiveShieldSpellBase - is
		///     untouched.
		/// </remarks>
		public async Task BreakShieldOnAggression(INetworkClient client)
		{
			if (client.Session.ActiveShieldSpellBase is not { } shieldBase)
				return;

			var self = client.Session.Character.Id;
			var appliedId = shieldBase << 8;

			// Retail shows the "lost the grace" on-screen line the instant the shield is lost to an aggressive action,
			// sent right before the buff-removal twin (conans-castle-exit rec#508); without it the icon vanishes with no
			// notification. Only fired here, on the aggression loss, never on a playfield swap.
			await WorldIir.Send(client, FormatFeedbackCodec.Build(self, new FeedbackLiteral(FeedbackXml.LostGraceEffect, FormatFeedbackCodec.EffectLineTypeFlags)));
			await SendBuffRemoval(client, self, self, appliedId); // deregisters the live buff too

			await SyncBuffStats(client);
			client.Session.ActiveShieldSpellBase = null;
			_logger.LogInformation("Shield of the Risen broken by aggressive action for {Name}.", client.Session.Character?.Name);
		}

		/// <summary>
		///     Re-applies a player's persisted self-buffs at world entry with their remaining time.
		/// </summary>
		/// <remarks>
		///     Reads the CharacterBuff snapshot taken at the last logout or autosave, drops any that lapsed while offline,
		///     rebuilds each ApplySpell payload from the persisted base id and effect nonce with a duration recomputed to
		///     the time left, and sends it through <see cref="SendMagicBuffIir" />, which re-registers the live ActiveBuff
		///     so the world tick expires it on schedule and the client counts down the correct remaining time. Runs in the
		///     post-0x205F flush beside RestoreMount and <see cref="SendShieldOfTheRisen" />, which own their own buffs and
		///     exclude them at save, so nothing is double-applied. A permanent buff (ExpiresAt null) restores with a 0
		///     duration and lives until cancelled.
		/// </remarks>
		public async Task RestoreBuffs(INetworkClient client)
		{
			var character = client.Session.Character;

			var buffs = await _stats.GetCharacterBuffs(character.Id);
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
						continue; // lapsed, or under 1 cs left, while logged out - do not re-apply
				}

				var spellBase = buff.BuffId >> 8;
				// Duration is the remaining time, not the spell's full duration, because that is what retail sends.
				// Measured over the corpus with `--report buff-durations`, which prints each spell's catalogue duration
				// beside every duration retail actually put on the wire: 51 of 256 spells carry at least one below-full
				// value, and they are exactly the long ones a session can span - `Contract of Protection` (full
				// 1,440,000 cs) appears as 1,180,241 / 1,195,185 / 1,213,465 / ... / 1,440,000, `XP Boost (4h)` as
				// 1,268,055, `Critical Rating Boost (4h)` as 431,043. Short buffs (Wrath 1000, Chill 600) only ever send
				// full, because they are never alive across a restore.
				//
				// Sending the full duration plus an absolute expiry and letting the client shorten it appears nowhere in
				// the corpus. The stamp is sourced from the same remaining time, which also keeps it correct before the
				// client has learned this server's clock: it learns that only from a DispatchPong, and world entry can
				// precede the first LatencyCheck.
				await SendMagicBuffIir(client, self, self, buff.BuffId, MagicBuffDatas.ApplyNow(spellBase, remainingCs, CasterLevel(client), casterIsPlayer: true));
				restored++;
			}

			if (restored > 0)
				_logger.LogInformation("Restored {Count} persisted buff(s) for {Name}.", restored, character.Name);
		}

		/// <summary>
		///     The server-to-client buff removal packet: the same ApplySpell (0x6B87E316) carrier as the apply, with the
		///     spell id's form byte set to 1 - the "end" twin - and an empty effect payload. The client clears the buff icon
		///     on receipt, so a server-initiated cancel, a dispel or the tick-driven expiry removes the icon live rather
		///     than only on relog.
		/// </summary>
		/// <remarks>
		///     Decoded from the mount-dismount captures (mount buff end 0x31eb1501 in *_dismount) and the combat captures
		///     (NPC-debuff ends 0x31644901 with applyTo=player, applyFrom=NPC). <paramref name="applyTo" /> and
		///     <paramref name="applyFrom" /> mirror the apply: equal for a self-buff, player and NPC respectively for an
		///     NPC-cast debuff. Byte-exact carrier content, after the IIR [u32 len=0x1A]:
		///     <code>
		///     [ApplySpell][c350][applyTo][u16 0][(base&lt;&lt;8)|1][c350][applyFrom]   (no payload)
		///     </code>
		///     The trailing 1 is the client's form byte (0 apply, 1 end, 2 update), not a rank - see
		///     docs/subsystems/npc-spell-applyspell.md.
		/// </remarks>
		public async Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId)
		{
			// Deregister first: "the buff ended" is a state change, not just a packet. Leaving the live `ActiveBuffs`
			// entry standing and expecting each caller to remove it means the server keeps a buff the client has been
			// told is over, the autosave persists it (a self-buff no dedicated re-applier owns, so `ShouldPersistBuff`
			// says yes) and the next login restores it - leaving the player with the effects and nothing to cancel.
			// Removing it here makes every path agree, and is a no-op for the callers that already did it: the tick's
			// expiry sweep takes the buff out of the dictionary before it sends, and `CancelBuff` needs the entry first
			// to read its caster.
			//
			// Only when the removal is about the player. This op also ends a debuff on a mob, and an unconditional
			// deregister would reach into the player's registry with the mob's spell id and drop a buff of theirs that
			// happens to share it. The world tick removes the mob's entry itself, as the player expiry sweep does.
			if (_sim.PlayerWorldEntity(client) is { } self && self.Handle == applyTo)
				self.ActiveBuffs.TryRemove(appliedSpellId, out _);

			var endId = (appliedSpellId & 0xFFFFFF00u) | 0x01u; // form 1 = the "end" twin of the applied buff id
			var body = MagicBuffCodec.Build(applyTo, applyFrom, endId, []);

			await WorldIir.Send(client, body);

			// The removal goes to viewers too, for the same reason the application does (SendMagicBuffIir relays it):
			// a buff the other clients were told to start must be told to stop, or their copy of it never ends. The
			// spellweave trance makes it obvious - a viewer holds the weave pose forever - but it applies to every buff.
			await _broadcaster.BroadcastSpellToViewers(client, body);
		}

		/// <summary>
		///     Sends the MagicBuffIIR (ApplySpell 0x6B87E316) carrier with an already-constructed payload and, when the
		///     buff lands on this client's own player, registers it so the server tracks it (.buffs, CancelBuff, tick
		///     expiry).
		/// </summary>
		/// <remarks>
		///     The duration is read back from the payload at +8, in centiseconds: a duration above 0 arms the tick to
		///     expire it and send the retail removal twin, while 0 means no tracked duration. Every ApplySpell send path
		///     funnels through here - the self-buff, the spellweave trance and component buffs, the mount ride buff and the
		///     BCC cast. Offensive casts that deal damage are handled by ResolveOffensiveCast instead.
		/// </remarks>
		public async Task SendMagicBuffIir(INetworkClient client, uint applyTo, uint applyFrom, uint spellId, MagicBuffData payload)
		{
			// A re-apply replaces, it does not stack. The client accumulates one icon per ApplySpell it receives, but
			// this registry is a dictionary keyed by the wire spell id, so N applications of the same spell collapse to
			// one entry. The tick then expires that one entry and sends one end twin, and every other icon of that spell
			// is orphaned: it counts to 0 and sticks there forever with nothing server-side that could remove it.
			//
			// The invariant is at most one live instance per spell id - teaching the registry to hold N stacks would
			// also need the end twin to identify which stack, and the twin carries an empty payload. Clearing the old
			// icon first makes a refresh look like a refresh: one icon with a restarted countdown, and never an orphan.
			// Retail's own NPC debuff cadence agrees, re-applying only after the previous one has ended.
			//
			// The subject test is "is this the client's own player", not "is the subject the caster". Testing
			// `applyTo == applyFrom` reads as a correctness guard but is really a scope restriction: it excludes every
			// record landing on the player from someone else, so an aura or a targeted cast from a mob would get no
			// dedup, no registry entry, no expiry and no end twin.
			if (_sim.PlayerWorldEntity(client) is { } live && live.Handle == applyTo && live.ActiveBuffs.TryRemove(spellId, out var previous))
			{
				await SendBuffRemoval(client, applyTo, previous.CasterHandle, spellId);
				_logger.LogDebug("Buff 0x{Buff:X8} re-applied to {Target:X8} - cleared the previous instance first so the client shows one icon, not a stack.", spellId, applyTo);
			}

			// The ApplySpell carrier body - the same layout for player and NPC casts (NpcSpellCastCodecTests). Built
			// once so the identical bytes go to the caster and to nearby viewers below.
			var body = MagicBuffCodec.Build(applyTo, applyFrom, spellId, payload);

			await WorldIir.Send(client, body);

			// Nearby players see the cast effect, not just the gesture. Retail streams the actor's ApplySpell to
			// observers who see the caster (other_playersvisible capture: other-player self-buffs and offensive casts).
			// The same body is reframed to each viewer who already sees the caster.
			await _broadcaster.BroadcastSpellToViewers(client, body);

			// The registry lives on the player's entity, so what decides whether a record belongs in it is whose bar it
			// lands on - which the `entity.Handle != applyTo` check below asks. A buff landing on a mob falls out
			// there, because the entity resolved is the client's player; the world tick owns a mob's registry
			// (`WorldSimulation.ApplyEffectToEntity`).
			//
			// Measured with `--report npc-debuff`: across the corpus retail's mob-onto-player records are 87 distinct
			// (player, mob, spell) pairings, a median of one apply each and never more than four, with the end twin
			// arriving on 86 of the 87. Retail applies once and closes it, which is what a registry entry plus the
			// expiry tick produce.

			// Only a timed buff is tracked. A zero-duration spell is an instant effect - the RDB states it by leaving
			// field 0x008 absent - not a permanent buff: it has nothing for TickBuffExpiry to expire, renders no icon,
			// and registering it would leak a never-expiring ActiveBuffs entry that also persists with a null ExpiresAt
			// and re-applies on every login. Every real buff has a finite duration (mount ride 24h, Shield 1h and 10h,
			// the longest being Barrel Blast at 1000h), so nothing legitimate needs a lives-until-cancelled path. This
			// mirrors the NPC-debuff path in WorldSimulation, which also tracks only a positive duration.
			//
			// `ApplySpell` returns before the send for a zero duration, so one reaches here only from the paths that
			// build a payload directly.
			var durationMs = ActiveBuff.DurationMsFromPayload(payload);
			if (durationMs == 0)
			{
				// An untracked apply: nothing will ever expire this id, so if the client does render an icon for it
				// the icon sticks at 0s forever. A zero-duration spell is taken to be an instant that renders no icon,
				// and this line is the first thing to check should a stuck icon ever be reported. The payload's
				// duration comes from Spell.DurationMs (RDB field 0x008), and 9,217 of the 20,848 spell rows carry
				// none - 69 of them while still carrying stat effects.
				_logger.LogDebug("Buff 0x{Buff:X8} applied to {Target:X8} with NO duration - not tracked, so it will never be expired or removed.", spellId, applyTo);
				return;
			}

			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null || entity.Handle != applyTo)
				return;

			entity.ActiveBuffs[spellId] = new ActiveBuff(spellId, applyFrom, DateTimeOffset.UtcNow, durationMs / 1000f);
			// The buff may carry stat deltas (armor, ratings, attributes) - fold them into the live stats.
			await SyncBuffStats(client);
		}

		/// <summary>
		///     Re-reads the character's equipped gear into the live entity's <see cref="WorldEntity.GearBonuses" /> and
		///     re-syncs the stats that derive from it. Call after any change to what is worn.
		/// </summary>
		/// <remarks>
		///     <c>GearBonuses</c> is a cache, written once by <c>RegisterPlayerInWorld</c> at world entry, and the buff
		///     recompute reads gear plus buffs off it - so without this an equip changes nothing until the next relog, and
		///     a buff applied afterwards re-asserts the stale gear half on top.
		///     <para>
		///         The refresh goes through <c>SetMaxFromBonus</c> (via the buff sync), not a reseed, so the semantics
		///         are right for gear: a bigger max leaves current where it is and fills by regen, a smaller one clamps
		///         current down. Equipping a +HP ring must not heal you, which is why this does not use
		///         <c>CombatService.ReseedFromMaxStats</c> - that one is for a level change, where full pools are correct.
		///     </para>
		///     <para>
		///         Nothing is pushed to the client. Retail's post-equip <c>0x96B8DC59</c> does carry the current pools
		///         (<c>0x001B</c>, <c>0x01F8</c>, <c>0x01FA</c>, never a max - a level-up is the contrary case, with all
		///         six), but the pool values here are wrong at the source, and emitting them makes the HUD render a mana
		///         bar an Assassin should not have. The recompute is the whole job until the values are right; there is
		///         more detail at the call site.
		///     </para>
		///     <para>
		///         Wired into equip and unequip, but not into the weapon-set swap in <c>WorldBroadcaster</c>, which
		///         changes <c>ActiveWeaponSet</c>: <c>GetEquippedStatItems(characterId, set)</c> is set-dependent, so
		///         swapping sets changes the gear aggregate just as equipping does. That needs a seam from the
		///         broadcaster back to here. Open work tracked in Gitea #3.
		///     </para>
		/// </remarks>
		public async Task RefreshGearBonuses(INetworkClient client)
		{
			if (_sim.PlayerWorldEntity(client) is not { } entity)
				return; // not in the world yet - world entry will build the aggregate itself

			var character = client.Session.Character;
			entity.GearBonuses = await CharacterStatBonuses.For(_stats, character);

			// Recompute the derived live fields off the new aggregate - max pools (which clamps current when a max
			// shrinks), magic damage, the regen rates. The returned change list is deliberately discarded: it is what a
			// buff change would push, and retail's post-equip update carries none of it.
			await _buffStats.Sync(entity);

			// Nothing is pushed to the client here, on purpose - the internal recompute above is the whole job.
			//
			// Retail does send the current pools after an equip (0x001B, 0x01F8, 0x01FA, never a max - the client
			// derives the max from the item itself). That is not done yet, because the values that would be sent are
			// wrong at the source: the current mana for a geared Assassin is 0 where retail's is 74, because the
			// snapshot takes it from the persisted CharacterMaxStats instead of the live geared vitals. Pushing
			// `ManaCurrent = 0` against the client's own max of 74 makes the HUD draw a mana bar the class does not
			// show - retail has that same 74 mana and no bar, because its pool is full rather than empty (retail sheet,
			// AssassinL23: `Mana 74 / 74`, no HUD bar).
			//
			// The push therefore waits on the pool values being right, which is a question about the base stats rather
			// than about equipping. Combat, regen and the bar-fill clamp all read the recomputed values above and are
			// unaffected.
		}

		/// <summary>
		///     Re-derives the player's buffed stats after any change to their live buffs - applied, expired, cancelled,
		///     broken, cleared on death - and pushes the ones that moved.
		/// </summary>
		/// <remarks>
		///     Called at every ActiveBuffs mutation rather than folded into the apply path, because a stat a buff raised has
		///     to come back down when it ends, and the end happens in four different places: tick expiry, .cancelbuff, the
		///     shield break and death. Cheap and quiet by design - most buffs carry no honoured stat delta, so Sync returns
		///     nothing and no packet is sent.
		/// </remarks>
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

		/// <summary>
		///     Applies Shield of the Risen at world entry: on a fresh login and on each changemap or teleport re-entry,
		///     because this runs from FlushPendingPost205FFollowUp, which the teleport path re-arms.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The client renders these top-of-screen as a protection banner rather than as a corner buff, and generates
		///         the "You are protected by Shield of the Risen" chat line from the ApplySpell itself - no separate server
		///         packet, confirmed absent from the capture. Byte-exact against NecroL80_..._conans-castle-exit rec#165
		///         (wire 0x3dfa4800, duration 0x57e40) and rec#736 (0x37f38c00, 0x36ee80). The durations resolve from the
		///         Spells rows, so <see cref="SendMagicBuffIir" /> arms the matching expiry and the buffs clear on time.
		///         They use the 54-byte buff-data form, as the spellweave and mount buffs do.
		///     </para>
		///     <para>
		///         Retail shows exactly one shield at a time, picked by playfield type: the 1h base 4061768 on an instanced
		///         playfield, the 10h base 3666828 in the shared open world - the castle-exit transition removes the 1h and
		///         applies the 10h (NecroL80_..._castle-exit rec#165, then rec#735 remove and rec#736 apply). Applying both
		///         at once makes the client render a second, description-less duplicate, so the prior shield's icon is
		///         removed with retail's form-1 end twin before the new one is applied. Every map is currently
		///         InstanceMode.Shared, so this resolves to the 10h everywhere.
		///     </para>
		/// </remarks>
		public async Task SendShieldOfTheRisen(INetworkClient client)
		{
			var self = client.Session.Character.Id;
			var map = await _world.GetMap(client.Session.Character.MapId);
			var desired = map?.InstanceMode is null or MapInstanceMode.Shared
				? ShieldOfTheRisenBase2 // 10h shared/open-world
				: ShieldOfTheRisenBase1; // 1h instanced

			if (client.Session.ActiveShieldSpellBase is { } prior && prior != desired)
				await SendBuffRemoval(client, self, self, prior << 8);

			await SendShieldBuff(client, self, desired);
			client.Session.ActiveShieldSpellBase = desired;
		}

		/// <summary>
		///     Walks the `CastNano` (53051) chain - "when this spell lands, also run X", on the same subject.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Distinct from the area elements beside it in the same table, which pick their own targets by radius; a
		///         chain edge states no radius at all, so reading one as an area sweeps a 0 m circle and reaches nobody.
		///         `Gangrenous Stench` is the clearest case: 221069 lands on a victim and its chain edge runs 3963058
		///         `Physical Wrack` at the same victim, which is the -10% Slashing/Piercing/Crushing/Poison the in-client
		///         tooltip shows.
		///     </para>
		///     <para>
		///         A chain edge's period comes from two fields the client names together in one log line ("Fixing broken
		///         gameaction data, Duration was 0 while Interval was N", FUN_0089b390): 0x2d6 is the interval and 0x2d5 the
		///         duration, where the duration is a repeat count - -1 unbounded, 1 once, and a thin tail of 2..40. Of the
		///         483 edges carrying an interval, 263 state -1, making the interval a cadence, and 220 state 1, making it
		///         a delay.
		///     </para>
		///     <para>
		///         This path runs every edge once when the carrier lands, which is the first run for both halves. The
		///         cadence half then re-runs from the aura tick (<c>WorldSimulation.EmitAuraAreas</c>); the delay half is
		///         unwired, because a delayed single run needs a one-shot timer the tick does not have.
		///     </para>
		/// </remarks>
		private async Task ApplyChainedSpells(INetworkClient client, uint applyTo, uint applyFrom, uint carrierBase, SpellChainWalk chain)
		{
			var subject = SubjectEntity(client, applyTo);

			foreach (var link in await _spellAreas.ChainedSpells(carrierBase))
			{
				// The edge's own condition run, which on a rank table is the selection itself. Each edge of such a
				// table is gated on `MustHavePassiveAbilityRunning <the rank below it>`, the edges are stored
				// highest-rank-first, and the top one ORs in itself - so evaluating in order and taking the first that
				// passes is a one-rank-per-proc upgrade: nothing running gets rank 1, rank 5 running gets rank 6, and
				// someone at the cap refreshes. Taking the walk's arrival order instead would hand the top of a ladder
				// to a character who had never proc'd it.
				var conditions = await _spellAreas.CriteriaFor(link);
				var unevaluated = 0;
				if (conditions.Count > 0 && !CriteriaEvaluator.Permits(conditions, SubjectStat, SubjectStat, out unevaluated, HasRunning))
				{
					_logger.LogDebug("Spell {Carrier}'s edge to {Chained} is gated by {Count} criteria that do not hold on 0x{Target:X8} - not applied.",
						carrierBase, link.EffectSpellId, conditions.Count, applyTo);
					continue;
				}

				if (unevaluated > 0)
				{
					_logger.LogDebug("Spell {Carrier}'s edge to {Chained} carries {Count} condition(s) this build cannot evaluate; applied (the conservative direction).",
						carrierBase, link.EffectSpellId, unevaluated);
				}

				// Claim the target's stacking line as well as its id. Ten edges into one line is a rank table, not ten
				// buffs, and the line is a mutual-exclusion group, so the first arrival wins and the rest are refused.
				// With the criteria above evaluated, "first" is the rung the ladder selects rather than whichever edge
				// happens to be listed first.
				if (!chain.TryEnter(link.EffectSpellId, await _spellAreas.StackingLineOf(link.EffectSpellId)))
					continue;

				_logger.LogDebug("Spell {Carrier} chains {Chained} onto 0x{Target:X8}.", carrierBase, link.EffectSpellId, applyTo);
				await ApplySpell(client, applyTo, applyFrom, link.EffectSpellId << 8, chain);
			}

			if (chain.Truncated)
				_logger.LogWarning("Spell {Carrier}'s chain hit the {Cap}-application cap and was truncated.", carrierBase, SpellChainWalk.MaxApplications);

			return;

			// `MustHavePassiveAbilityRunning` names a spell, and the registry is keyed by the wire id
			// (`base << 8`), so the comparison has to shift rather than equate. An unresolvable subject answers
			// false rather than null: an entity with no registry has no buff running, which is a fact about it,
			// where letting the condition through unevaluated would put the whole ladder back on arrival order.
			bool HasRunning(uint spellBase)
			{
				if (subject == null)
					return false;

				foreach (var wireId in subject.ActiveBuffs.Keys)
				{
					if (wireId >> 8 == spellBase)
						return true;
				}

				return false;
			}

			// The subject's sheet for the comparison operators, on the same null-means-unevaluated contract
			// BuffStatEffects uses. Reading an absent stat as 0 would fail every `> n` gate and drop edges that
			// legitimately apply.
			int? SubjectStat(CharacterStatId stat)
			{
				if (subject == null)
					return null;

				return stat switch
				{
					CharacterStatId.Level => (int)subject.Level,
					CharacterStatId.HealthCurrent => (int?)subject.Vitals?.Health,
					_ => subject.BuffedStats.TryGetValue(stat, out var value) ? (int)value : null
				};
			}
		}

		/// <summary>
		///     The entity a spell is landing on, by handle: the player themselves or anything in their zone. Null when the
		///     caller is not in the world yet, or when the handle names something the zone no longer holds - a mob that died
		///     between the cast and the landing.
		/// </summary>
		private WorldEntity? SubjectEntity(INetworkClient client, uint handle)
		{
			if (_sim.PlayerWorldEntity(client) is not { } player)
				return null;

			return player.Handle == handle
				? player
				: _sim.World.ZoneForEntity(player).TryGet(handle, out var other)
					? other
					: null;
		}

		private async Task SendShieldBuff(INetworkClient client, uint self, uint spellBase)
		{
			var spell = await _catalog.GetSpell(spellBase);
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
