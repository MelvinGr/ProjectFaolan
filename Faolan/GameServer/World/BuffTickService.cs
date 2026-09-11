using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The per-tick life of an applied buff: it expires, it ticks damage or healing, and while it is up it may
	///     project an aura area that applies further effects to whoever stands in it.
	/// </summary>
	/// <remarks>
	///     It answers "what does a buff that is already on someone do over time", as against <c>BuffService</c>'s "what
	///     happens when one is applied or removed" and <see cref="WorldSimulation" />'s "what is in the world this
	///     tick". The dependency runs one way: it holds the two pieces of world state it reads - the player registry
	///     and the zone model - rather than the simulation, so the tick calls in and nothing calls back.
	///     <para>
	///         It owns the registry of non-player entities carrying a buff, because that map exists for this loop and
	///         nothing else reads it. Its outbound port is bound at <see cref="WorldSimulation.Start" />, as its
	///         siblings' are.
	///     </para>
	/// </remarks>
	public sealed class BuffTickService
	{
		/// <summary>The fallback damage-over-time cadence, for a spell whose interval the captures never established.</summary>
		/// <remarks>
		///     It is the median of the 53 measured intervals, which is the most defensible value for the 31 still
		///     unmeasured; see <c>Spell.DotIntervalMs</c> for the per-spell value where one exists. It is deliberately
		///     not the most common value - the measured set peaks at 1 s (16 spells) against 2 s (14) - and it is still
		///     wrong for any individual spell that falls back to it.
		/// </remarks>
		private const double DotTickSeconds = 2.0;

		// The damage-over-time effect of each base spell, looked up once and reused so the per-cast NPC path does not
		// hit the database. A delta of 0 means the spell is not a DoT.
		private readonly ConcurrentDictionary<uint, (int Delta, uint Type, uint IntervalMs)> _dotEffects = new ConcurrentDictionary<uint, (int Delta, uint Type, uint IntervalMs)>();

		// The last aura application per (holder, buff, area). See TickAuras for why it is not WorldEntity.LastDotTick;
		// the holder is in the key because a mob can carry an area-emitting debuff, so two entities holding the same
		// buff would otherwise share one stamp and each suppress the other's cadence.
		private readonly Dictionary<(uint Holder, uint BuffId, uint Sequence), DateTimeOffset> _auraTicks = [];

		/// <summary>Converts a stored damage-over-time delta from wire units to the display units this layer works in.</summary>
		/// <remarks>
		///     <c>Spell.DotDelta</c> is a captured damage-record delta and so is a wire number, while everything it
		///     meets here is display: <c>EntityVitals.Health</c> is the vitals value divided by 100, the melee path
		///     spends an already converted figure, and the damage record reports its delta beside that same display
		///     health. Unconverted, a flat captured constant is devastating - <c>Rend</c> ticks -54 every 1.5 s, which
		///     empties a level-1 player's 88-point pool in three ticks, and the corpus goes to -258,352 for
		///     <c>Chill (Rank 5)</c>, which no display pool survives.
		///     <para>
		///         The conversion happens here, at the one place a spell row becomes a live effect, rather than at the
		///         tick: the value then reaches both the health application and the wire already in display units, so
		///         the two cannot drift apart. The stored column stays in wire units, because the captured evidence is
		///         wire and rewriting it would make the provenance unreadable.
		///     </para>
		///     <para>
		///         The magnitude is floored at 1 rather than truncated, so a small DoT still does something:
		///         <c>Rend</c>'s -54 is 0 under integer division, and a debuff that ticks for nothing reads as broken
		///         rather than scaled. The sign is preserved, so a heal-over-time floors at +1 by the same rule.
		///     </para>
		/// </remarks>
		private static int DisplayDotDelta(int wireDelta)
		{
			if (wireDelta == 0)
				return 0;

			var scaled = wireDelta / (int)CharacterStatId.HealthMax.GetScale();

			return scaled != 0 ? scaled : Math.Sign(wireDelta);
		}

		private readonly ICatalogRepository _database;
		private readonly ILogger _logger;
		private readonly PlayerRegistry _players;
		private readonly SpellAreaCatalog _spellAreas;
		private readonly WorldState _world;

		private IWorldOutbound _output = null!;

		public BuffTickService(WorldState world, PlayerRegistry players, ICatalogRepository database, SpellAreaCatalog spellAreas, ILogger logger)
		{
			_world = world;
			_players = players;
			_database = database;
			_spellAreas = spellAreas;
			_logger = logger;
		}

		/// <summary>Binds the outbound port. Called from <see cref="WorldSimulation.Start" />, once.</summary>
		public void BindOutbound(IWorldOutbound output)
		{
			_output = output;
		}

		// The non-player entities currently carrying a buff, so the tick can find them without walking every zone.
		// Written where a buff lands on one and pruned by the tick as they empty, die or despawn: in practice a handful
		// of mobs mid-fight, never the world.
		private readonly ConcurrentDictionary<uint, WorldEntity> _buffedEntities = new ConcurrentDictionary<uint, WorldEntity>();

		public async Task<(int Delta, uint Type, uint IntervalMs)> DotEffect(uint baseSpellId)
		{
			if (_dotEffects.TryGetValue(baseSpellId, out var cached))
				return cached;

			var spell = await _database.GetSpell(baseSpellId);
			var effect = (DisplayDotDelta(spell?.DotDelta ?? 0), spell?.DotType ?? 0, spell?.DotIntervalMs ?? 0);
			_dotEffects[baseSpellId] = effect;
			return effect;
		}

		public async Task TickBuffExpiry()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in _players)
			{
				// Decide and remove in one snapshot pass before sending anything. Removing inside a foreach over the
				// live concurrent dictionary, with awaits in the body, can skip a buff whose bucket neighbour has just
				// been removed, leaving some of a tick's expiries behind with their icon stuck at 0 s.
				var expired = pair.Value.TakeExpiredBuffs(now);
				if (expired.Count == 0)
					continue;

				// Log the batch before sending any of it: the failure mode to diagnose is several buffs expiring
				// together and only some icons clearing, so this count against the per-buff lines below says whether
				// the tick missed one or sent them all and the client kept an icon.
				if (expired.Count > 1)
					_logger.LogInformation("Buffs: {Count} expired together on {Player} ({Ids}).", expired.Count, pair.Key.Session.Character?.Name, string.Join(", ", expired.Select(b => $"0x{b.Key:X8}")));

				foreach (var buff in expired)
				{
					_logger.LogInformation("Buff 0x{Buff:X8} expired on {Player}.", buff.Key, pair.Key.Session.Character?.Name);
					// Mirror the apply: applyTo is the affected player and applyFrom the caster - the player for a
					// self-buff, the NPC for an NPC-cast debuff, which is what CasterHandle carries.
					await _output.SendBuffRemoval(pair.Key, pair.Value.Handle, buff.Value.CasterHandle, buff.Key);
				}

				// The buffs' stat deltas come off with them. One sync after the batch: it recomputes gear plus all
				// remaining buffs from scratch, so a per-buff sync would push the same end state several times.
				await _output.SyncBuffStats(pair.Key);
			}
		}

		/// <summary>
		///     This buff's own damage-over-time interval, or <see cref="DotTickSeconds" /> where the captures never
		///     showed one.
		/// </summary>
		private static double IntervalOf(ActiveBuff buff)
		{
			return buff.DotIntervalMs > 0
				? buff.DotIntervalMs
				: DotTickSeconds * 1000;
		}

		/// <summary>
		///     The aura: a buff that re-applies an effect to everyone around its holder, every period, for as long as
		///     it is up.
		/// </summary>
		/// <remarks>
		///     <c>Gangrenous Stench</c> is the shape - the cast puts a 25 s buff on the caster, and that buff hits a
		///     7 m radius every 4 s. The spell applies an effect to self, and that effect applies to enemies nearby,
		///     where <c>Life Leech</c> applies to enemies once at cast and is done. It is distinct from
		///     <see cref="TickDots" />, which ticks a damage-over-time on the entity that owns the buff and never walks
		///     a radius.
		///     <para>
		///         The period is per area rather than per buff, because a spell may state several with different
		///         periods, so the stamp is keyed by both. <c>LastDotTick</c> is deliberately not reused: a spell can
		///         carry both a DoT and an aura, and sharing one stamp would make each suppress the other's cadence.
		///     </para>
		/// </remarks>
		public async Task TickAuras()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in _players)
			{
				var entity = pair.Value;
				var client = pair.Key;

				var vitals = entity.Vitals;
				if (vitals == null || vitals.IsDead)
					continue;

				await EmitAuraAreas(entity, client, now);
			}

			// A buff that ended leaves its stamp behind; drop the ones whose buff is gone so this cannot grow without
			// bound across a long session. Mobs count too, because an infected one emits its own areas.
			if (_auraTicks.Count > 0)
			{
				var live = _players.Values.Concat(_buffedEntities.Values).SelectMany(e => e.ActiveBuffs.Keys.Select(id => (e.Handle, id))).ToHashSet();
				foreach (var key in _auraTicks.Keys.Where(k => !live.Contains((k.Holder, k.BuffId))).ToList())
					_auraTicks.Remove(key);
			}
		}

		/// <summary>
		///     The periodic half of one entity's buffs: for every buff it carries that states a periodic area, re-apply
		///     that area once its period has elapsed.
		/// </summary>
		/// <remarks>
		///     Shared by the player aura tick and the mob tick, because the mechanic does not care who holds the buff:
		///     <c>Gangrenous Stench</c> is a player self-buff whose area lands a debuff on mobs, and that debuff states
		///     an area of its own that spreads from the mob it is sitting on. Each emission is attributed to the buff's
		///     own caster, so a mob carrying debuffs from two players emits each one for the player who put it there.
		/// </remarks>
		/// <param name="holderClient">
		///     The fallback for a buff whose caster is not a connected player: the holder themselves, which is what a
		///     player carrying an NPC-cast area debuff wants, and null for a mob, where the caster is the only credit
		///     there is.
		/// </param>
		private async Task EmitAuraAreas(WorldEntity holder, INetworkClient? holderClient, DateTimeOffset now)
		{
			foreach (var buff in holder.ActiveBuffs.Values.ToArray())
			{
				// The buff id is the wire form, (base << 8) | rank; the catalog is keyed by the base.
				var areas = await _spellAreas.AuraAreas(buff.BuffId >> 8);
				if (areas.Count == 0)
					continue;

				var client = ClientForHandle(buff.CasterHandle) ?? holderClient;
				if (client == null)
					continue;

				foreach (var area in areas)
				{
					var key = (holder.Handle, buff.BuffId, area.Sequence);
					if (_auraTicks.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < area.PeriodCentiseconds * 10)
						continue;

					_auraTicks[key] = now;

					// God mode blocks harm, and an aura is harm the holder emits rather than takes; the recipients
					// are covered because the area application goes through the NPC damage path, which owns that
					// check for the targets, so nothing extra is needed here.
					//
					// Logged even when it reaches nothing: guarding the log on a non-zero hit count would make a
					// tick that found no targets indistinguishable from a tick that never ran, which is the first
					// question asked of an aura that looks inert.
					var hit = await ApplyAreaEffect(client, holder.Handle, buff.CasterHandle, area);
					_logger.LogDebug("Aura from buff 0x{Buff:X8} on 0x{Holder:X8} applied {Effect} to {Hit} target(s) within {Radius}m.", buff.BuffId, holder.Handle,
						area.EffectSpellId, hit, area.RadiusMetres);
				}

				// A CastNano chain edge can state a cadence too, and 263 of them do. It is not an area - it re-runs on
				// the holder, with no radius - so it shares this tick and the aura stamp, whose key carries the row's
				// own Sequence so the two kinds cannot collide, but not the area application.
				//
				// Only the repeats-forever half belongs here. The other 220 interval-carrying edges repeat once, which
				// runs after a delay, and a delay needs a one-shot timer this tick does not have; running them here
				// would turn a single delayed cast into a permanent cadence.
				foreach (var edge in await _spellAreas.RepeatingChains(buff.BuffId >> 8))
				{
					var key = (holder.Handle, buff.BuffId, edge.Sequence);
					if (_auraTicks.TryGetValue(key, out var lastChain) && (now - lastChain).TotalMilliseconds < edge.PeriodCentiseconds * 10)
						continue;

					_auraTicks[key] = now;

					// The same re-apply guard as every other landing: a holder still carrying the chained effect keeps
					// the live one counting down rather than re-asserting it, which is retail's rule of re-applying
					// only after the previous has ended. The cadence is therefore a refresh schedule, not a stack.
					await ApplyEffectToEntity(client, holder.Handle, buff.CasterHandle, edge.EffectSpellId);
					_logger.LogDebug("Chain from buff 0x{Buff:X8} on 0x{Holder:X8} re-ran {Effect} (every {Period} cs).", buff.BuffId, holder.Handle,
						edge.EffectSpellId, edge.PeriodCentiseconds);
				}
			}
		}

		/// <summary>
		///     Apply one of a spell's area elements whole: the damage to everything in range, and then the effect
		///     itself on each of them.
		/// </summary>
		/// <remarks>
		///     Both halves are needed. Dealing only the damage leaves the effect record the area names landing on
		///     nobody, so it has no damage-over-time to bleed, no stat deltas to apply, and its own nested area is
		///     never reached - which is <c>Gangrenous Stench</c> hitting for its initial number and then doing nothing.
		/// </remarks>
		/// <param name="sourceHandle">What the area radiates from: the buff's holder.</param>
		/// <param name="casterHandle">
		///     Who gets the credit, the player for the whole chain even where the emitter is an infected mob: a bleed
		///     that finishes a mob has to name a killer the experience and loot lifecycle can pay.
		/// </param>
		public async Task<int> ApplyAreaEffect(INetworkClient client, uint sourceHandle, uint casterHandle, SpellAreaEffect area)
		{
			var victims = await _output.ApplySpellArea(client, sourceHandle, area.EffectSpellId, area.RadiusMetres, area.MaxTargets);
			foreach (var handle in victims)
				await ApplyEffectToEntity(client, handle, casterHandle, area.EffectSpellId);

			return victims.Count;
		}

		/// <summary>
		///     Land an effect spell on one non-player victim: the ApplySpell carrier, so the client draws the icon on
		///     it, and a tracked <see cref="ActiveBuff" />, so the tick can count it down, bleed its damage-over-time
		///     and emit its own areas. The mirror of what <c>NpcCombatService.ApplyNpcCast</c> does for a debuff an NPC
		///     puts on a player.
		/// </summary>
		private async Task ApplyEffectToEntity(INetworkClient client, uint victimHandle, uint casterHandle, uint effectSpellId, SpellChainWalk? chain = null)
		{
			if ((_players.TryGetValue(client, out var pe) ? pe : null) is not { } player || !_world.ZoneForEntity(player).TryGet(victimHandle, out var victim) || victim.IsPlayer || victim.IsDead)
				return;

			// Seed the chain walk with the record landing here, so an edge pointing back at it terminates. It is null
			// at an original landing - an area applying its effect - and non-null only when this call is a chain edge.
			chain ??= new SpellChainWalk();
			chain.TryEnter(effectSpellId);

			var spell = await _database.GetSpell(effectSpellId);
			var durationMs = spell?.DurationMs ?? 0;
			var wireId = effectSpellId << 8;

			// A zero duration is an instant: retail sends no ApplySpell for one, and one aborts the client's
			// ActivateBuff. It still landed, so its chain runs below - the icon is skipped, not the effect.
			if (durationMs > 0)
			{
				// Already carrying it: leave the live one counting down rather than re-sending the apply. Re-applying
				// on every period is what stacks icons on the client - retail re-applies an effect only after the
				// previous one has ended - and the area's damage lands either way.
				//
				// The chain is skipped with it, deliberately: nothing new landed on this victim, so re-running the
				// edge would re-assert the chained debuff on every aura period.
				if (victim.ActiveBuffs.ContainsKey(wireId))
					return;

				var (dotDelta, dotType, dotIntervalMs) = await DotEffect(effectSpellId);
				var now = DateTimeOffset.UtcNow;
				victim.ActiveBuffs[wireId] = new ActiveBuff(wireId, casterHandle, now, durationMs / 1000f)
				{
					DotDelta = dotDelta,
					DotType = dotType,
					DotIntervalMs = dotIntervalMs,
					LastDotTick = now
				};

				TrackBuffedEntity(victim);
				await _output.SendSpellApplication(client, victim.Handle, casterHandle, wireId, durationMs / 10);
				_logger.LogDebug("Effect {Effect} applied to {Npc} (0x{Handle:X8}) for {Duration}ms by 0x{Caster:X8}.", effectSpellId, victim.Name, victim.Handle, durationMs, casterHandle);
			}

			// The CastNano chain: "this landed, so also run X" on the same victim, with no radius of its own -
			// Gangrenous Stench's 221069 reaching Physical Wrack 3963058 is the headline case. Every edge runs once
			// here; the ones that state a cadence re-run from the aura tick, in EmitAuraAreas.
			foreach (var link in await _spellAreas.ChainedSpells(effectSpellId))
			{
				// The target's stacking line is claimed with its id: ten edges into one line is a rank table, not ten
				// buffs. The same rule as the player-side walk in BuffService.
				if (!chain.TryEnter(link.EffectSpellId, await _spellAreas.StackingLineOf(link.EffectSpellId)))
					continue;

				_logger.LogDebug("Effect {Effect} chains {Chained} onto {Npc} (0x{Handle:X8}).", effectSpellId, link.EffectSpellId, victim.Name, victim.Handle);
				await ApplyEffectToEntity(client, victim.Handle, casterHandle, link.EffectSpellId, chain);
			}

			if (chain.Truncated)
				_logger.LogWarning("Effect {Effect}'s chain hit the {Cap}-application cap on {Npc} and was truncated.", effectSpellId, SpellChainWalk.MaxApplications, victim.Name);
		}

		/// <summary>
		///     Registers a non-player entity whose <c>ActiveBuffs</c> <see cref="TickNpcBuffs" /> has to service.
		/// </summary>
		/// <remarks>
		///     The only writer is the effect-application path above. It is a separate method so the registry stays
		///     private, and so a test can put an entity in front of one tick pass with no database behind it.
		/// </remarks>
		public void TrackBuffedEntity(WorldEntity entity)
		{
			_buffedEntities[entity.Handle] = entity;
		}

		/// <summary>The world tick for buffs on non-player entities.</summary>
		/// <remarks>
		///     <see cref="TickBuffExpiry" />, <see cref="TickDots" /> and <see cref="TickAuras" /> all walk the player
		///     registry, so without this a debuff a player put on a mob has nothing to expire it, nothing to bleed it
		///     and nothing to emit its own areas: it counts down on the client to 0 and sticks there.
		///     <para>
		///         Everything here is attributed to the caster's player, and if they have gone - logged out, or changed
		///         playfield - the buff is dropped rather than left ticking, because there is no client left to render
		///         its end or to be paid for a kill it lands.
		///     </para>
		/// </remarks>
		public async Task TickNpcBuffs()
		{
			if (_buffedEntities.IsEmpty)
				return;

			var now = DateTimeOffset.UtcNow;
			foreach (var (handle, entity) in _buffedEntities.ToArray())
			{
				// Dead, despawned or clean: the entity leaves the registry. A corpse renders no icon, and a bleed that
				// kept ticking into a kill lifecycle that has already run would pay the experience twice.
				if (entity.ActiveBuffs.IsEmpty || entity.IsDead || !_world.ZoneForEntity(entity).TryGet(handle, out _))
				{
					entity.ActiveBuffs.Clear();
					_buffedEntities.TryRemove(handle, out _);
					continue;
				}

				// Expire first, in one snapshot pass, for the same reason TickBuffExpiry does it that way.
				foreach (var expired in entity.TakeExpiredBuffs(now))
				{
					if (ClientForHandle(expired.Value.CasterHandle) is { } owner)
						await _output.SendBuffRemoval(owner, entity.Handle, expired.Value.CasterHandle, expired.Key);

					_logger.LogDebug("Effect 0x{Buff:X8} expired on {Npc} (0x{Handle:X8}).", expired.Key, entity.Name, entity.Handle);
				}

				foreach (var buff in entity.ActiveBuffs.Values.ToArray())
				{
					var owner = ClientForHandle(buff.CasterHandle);
					if (owner == null)
					{
						entity.ActiveBuffs.TryRemove(buff.BuffId, out _);
						continue;
					}

					if (buff.DotDelta != 0 && (now - buff.LastDotTick).TotalMilliseconds >= IntervalOf(buff))
					{
						buff.LastDotTick = now;
						await _output.ApplyNpcDotTick(owner, entity.Handle, buff.CasterHandle, buff.DotDelta, buff.BuffId >> 8);
						if (entity.IsDead)
							break; // the bleed landed the killing blow, and the sweep above retires it next tick
					}
				}

				// The chain: an effect record can state an area of its own, so an infected mob spreads it to its
				// neighbours. The same operation the player aura uses, centred on the mob.
				if (!entity.IsDead)
					await EmitAuraAreas(entity, null, now);
			}
		}

		/// <summary>
		///     The connected player whose world entity carries <paramref name="handle" />, or null when nobody does -
		///     they logged out, or the handle belongs to a mob. The tick's way back to a client from a buff's recorded
		///     caster.
		/// </summary>
		private INetworkClient? ClientForHandle(uint handle)
		{
			foreach (var pair in _players)
			{
				if (pair.Value.Handle == handle)
					return pair.Key;
			}

			return null;
		}

		public async Task TickDots()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in _players)
			{
				var entity = pair.Value;
				var client = pair.Key;
				var character = client.Session.Character;

				var vitals = entity.Vitals;
				if (vitals == null || vitals.IsDead)
					continue;

				foreach (var buff in entity.ActiveBuffs.Values)
				{
					if (buff.DotDelta == 0 || (now - buff.LastDotTick).TotalMilliseconds < IntervalOf(buff))
						continue;

					buff.LastDotTick = now;

					// God mode has to cover damage over time, not only the swing that applied it: the NPC cast path
					// creates the buff before its own god-mode return, so without this guard a bleed applied through
					// god mode goes on to kill the player. The tick is the single choke point every damage-over-time
					// source funnels through, which is why the guard belongs here rather than at each application
					// site.
					//
					// The stamp above still advances, so a debuff that ticked through god mode does not burst-catch-up
					// the moment it is switched off. Heals over time are unaffected: god mode blocks harm, not healing.
					if (entity.GodMode && buff.DotDelta < 0)
						continue;

					vitals.ApplyHealthDelta(buff.DotDelta);
					await _output.SendStatDamage(client, entity.Handle, vitals.Health, buff.DotDelta, buff.DotType, buff.CasterHandle,
						buff.BuffId >> 8); // a health record names the real base spell id, as retail's does
					await _output.PushVitals(client, vitals);

					if (vitals.IsDead)
					{
						entity.LastAttackerHandle = buff.CasterHandle; // a bleed can land the killing blow
						_logger.LogInformation("Combat: {Player} bled out from buff 0x{Buff:X8}.", character.Name, buff.BuffId);
						break;
					}
				}
			}
		}
	}
}
