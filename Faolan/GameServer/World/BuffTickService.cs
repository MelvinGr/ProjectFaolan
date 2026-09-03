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
	// The per-tick LIFE OF AN APPLIED BUFF (#900, cut out of WorldSimulation): it expires, it ticks damage or
	// healing, and while it is up it may project an aura area that applies further effects to whoever stands in it.
	//
	// The question it answers is "what does a buff that is already on someone do over time" - as against
	// `BuffService`'s "what happens when one is applied or removed", and against `WorldSimulation`'s "what is in the
	// world this tick". ⚠️ The dependency runs ONE WAY: it holds the two pieces of world state it reads (the player
	// registry and the zone model) rather than the simulation, so the tick calls in and nothing calls back.
	//
	// It owns `_buffedEntities` - the NPCs carrying a buff that are not otherwise being ticked - because that map
	// exists for exactly this loop and nothing else reads it.
	//
	// Its outbound port is BOUND at Start, like the simulation's and the death service's: everything it reaches is
	// built ON the world model. See WorldSimulation._output.
	public sealed class BuffTickService
	{
		// Applies damage/heal-over-time from active DoT debuffs (#274). Each tracked buff with a DotDelta bleeds (or
		// heals) the target's HealthCurrent on a fixed cadence, mirroring the captured StatDamageIIR cycle: the tick
		// moves the player's vitals by the delta and sends the byte-exact StatDamageIIR (+ a PushVitals for the bar).
		// The fallback DoT cadence, for a spell whose interval the captures never established. This was once a guess
		// ("bleeds tick roughly every couple of seconds") applied to EVERY DoT, until the interval was measured per
		// spell (#437) - see Spell.DotIntervalMs.
		//
		// It survived the measurement as the MEDIAN of the 53 intervals now measured, which is the most defensible
		// thing to guess for the 31 still unmeasured. Worth knowing that it is not the most COMMON value: the
		// measured set peaks at 1 s (16 spells) against 2 s (14), so the fallback is deliberately the middle of the
		// range rather than its mode, and it is still wrong for any individual spell that lands on it.
		private const double DotTickSeconds = 2.0;

		// Cached DoT effect per base spell id (Spell.DotDelta/DotType): looked up once per spell, then reused so the
		// per-cast NPC path doesn't hit the DB. (delta 0 = not a DoT.)
		private readonly ConcurrentDictionary<uint, (int Delta, uint Type, uint IntervalMs)> _dotEffects = new ConcurrentDictionary<uint, (int Delta, uint Type, uint IntervalMs)>();

		// Last aura application per (holder, buff, area) - see TickAuras for why it is not WorldEntity.LastDotTick, and
		// #776 for why the HOLDER is in the key: since a mob can carry an area-emitting debuff, two entities holding
		// the same buff would otherwise share one stamp and each suppress the other's cadence.
		private readonly Dictionary<(uint Holder, uint BuffId, uint Sequence), DateTimeOffset> _auraTicks = [];

		// `Spell.DotDelta` is a captured `StatDamageIIR` delta, so it is a WIRE number - and everything it meets at
		// this layer is DISPLAY: `EntityVitals.Health` is the ÷100 vitals value, the melee path spends the already
		// converted `CombatResolver.ResolveNpcAttackDamage`, and `SendStatDamage` reports the delta beside that same
		// display `vitals.Health`. This is #572's unit mismatch one path over, and it bites harder: melee was a curve
		// we could reason about, while a DoT is a flat captured constant. `Rend` (3236937) ticks -54 every 1.5s, so a
		// level-1 Scavenger Lackey bled a level-1 player's 88-point pool dry in three ticks - the "level-1 mob kills a
		// level-1 player" report that #572 was filed for, still reproducing through the buff path after the melee half
		// was fixed. The corpus goes to -258,352 (`Chill (Rank 5)`), which no display pool survives at all.
		//
		// Converted HERE, at the one place a spell row becomes a live effect, rather than at the tick: the value then
		// reaches BOTH the health application and the wire already in display units, so the two cannot drift apart.
		// The stored column stays in wire units for the same reason #572 left its curve alone - the captured evidence
		// is wire, and rewriting it would make the provenance unreadable.
		//
		// Magnitude is floored at 1 rather than truncated, so a small DoT still does something: `Rend`'s -54 is 0 under
		// integer division, and a debuff that ticks for nothing would read as "DoTs are broken" instead of "scaled".
		// Sign is preserved - a positive delta is a heal-over-time, which floors at +1 by the same rule.
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

		// #776: the NON-PLAYER entities currently carrying a buff, so the tick can find them without walking every
		// zone. Written where a buff lands on one and pruned by the tick as they empty, die or despawn - in practice a
		// handful of mobs mid-fight, never the world.
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
				// Decide and remove in one snapshot pass BEFORE sending anything (WorldEntity.TakeExpiredBuffs). The old
				// shape - TryRemove inside a foreach over the live ConcurrentDictionary, with awaits in the body - could
				// skip a buff whose bucket neighbour had just been removed, so several buffs expiring in the same tick
				// left some behind with their icon stuck at 0s.
				var expired = pair.Value.TakeExpiredBuffs(now);
				if (expired.Count == 0)
					continue;

				// Log the BATCH before sending any of it: "several expired together and only some icons cleared" is the
				// reported failure mode, so the count here vs the per-buff lines below says immediately whether the tick
				// missed one (server side) or sent all of them and the client kept an icon (client side).
				if (expired.Count > 1)
					_logger.LogInformation("Buffs: {Count} expired together on {Player} ({Ids}).", expired.Count, pair.Key.Session.Character?.Name, string.Join(", ", expired.Select(b => $"0x{b.Key:X8}")));

				foreach (var buff in expired)
				{
					_logger.LogInformation("Buff 0x{Buff:X8} expired on {Player}.", buff.Key, pair.Key.Session.Character?.Name);
					// Mirror the apply: applyTo = the affected player, applyFrom = the caster (self for a self-buff,
					// the NPC for an NPC-cast debuff). CasterHandle carries the applyFrom (#274).
					await _output.SendBuffRemoval(pair.Key, pair.Value.Handle, buff.Value.CasterHandle, buff.Key);
				}

				// #367: the buffs' stat deltas (if any) come off with them. ONE sync after the batch - it recomputes
				// `gear + all remaining buffs` from scratch, so per-buff syncs were redundant work pushing the same
				// end state several times.
				await _output.SyncBuffStats(pair.Key);
			}
		}

		// This DoT's own tick interval, or the 2 s fallback where the captures never showed one.
		private static double IntervalOf(ActiveBuff buff)
		{
			return buff.DotIntervalMs > 0
				? buff.DotIntervalMs
				: DotTickSeconds * 1000;
		}

		// #772: the AURA - a buff that re-applies an effect to everyone around its holder, every period, for as long
		// as it is up. `Gangrenous Stench` is the shape: the cast puts a 25 s buff on the CASTER, and that buff hits a
		// 7 m radius every 4 s. The maintainer's framing of the mechanic, which is what separates it from everything else here:
		// the spell applies an effect to SELF, and that effect applies to enemies nearby - where `Life Leech` applies
		// to enemies once, at cast, and is done.
		//
		// ⚠️ This is a mechanic the server had NO form of. `TickDots` ticks a DoT on the entity that OWNS the buff;
		// nothing walked a buff's radius applying an effect to OTHERS. So every aura in the game was inert - the buff
		// icon appeared and did nothing, which is exactly what Gangrenous Stench did.
		//
		// The period is per AREA, not per buff (a spell may state several with different periods), so the stamp is
		// keyed by both. `LastDotTick` is deliberately not reused: a spell can carry a DoT AND an aura, and sharing one
		// stamp would make each suppress the other's cadence.
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
			// bound across a long session. Mobs count too since #776 - an infected one emits its own areas.
			if (_auraTicks.Count > 0)
			{
				var live = _players.Values.Concat(_buffedEntities.Values).SelectMany(e => e.ActiveBuffs.Keys.Select(id => (e.Handle, id))).ToHashSet();
				foreach (var key in _auraTicks.Keys.Where(k => !live.Contains((k.Holder, k.BuffId))).ToList())
					_auraTicks.Remove(key);
			}
		}

		// The periodic half of one entity's buffs: for every buff it carries that states a periodic area, re-apply
		// that area if its period has elapsed. Shared by the player aura tick and the #776 mob tick, because the
		// mechanic does not care who holds the buff - `Gangrenous Stench` is a player self-buff whose area lands a
		// debuff on mobs, and THAT debuff states an area of its own that spreads from the mob it is sitting on.
		//
		// Each emission is attributed to the buff's own caster, so a mob carrying debuffs from two players emits each
		// one for the player who put it there. `holderClient` is the fallback for a buff whose caster is not a
		// connected player - the holder themselves, which is what a player carrying an NPC-cast area debuff wants,
		// and null for a mob (there the caster is the only credit there is).
		private async Task EmitAuraAreas(WorldEntity holder, INetworkClient? holderClient, DateTimeOffset now)
		{
			foreach (var buff in holder.ActiveBuffs.Values.ToArray())
			{
				// The buff id is the WIRE form ((base << 8) | rank); the catalog is keyed by the base.
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

					// God mode blocks harm, and an aura is harm the holder is emitting rather than taking - but the
					// same reasoning as TickDots applies to the RECIPIENTS, and ApplySpellArea goes through
					// ApplyNpcDamage, which owns that check for the targets. Nothing extra is needed here.
					// ⚠️ Logged even when it reaches NOTHING. Guarding this on `hit > 0` made a tick that found no
					// targets indistinguishable from a tick that never ran, and that is exactly the question asked
					// of it first ("the aura only fired twice" - it had fired six times and killed everything in
					// range by the third).
					var hit = await ApplyAreaEffect(client, holder.Handle, buff.CasterHandle, area);
					_logger.LogDebug("Aura from buff 0x{Buff:X8} on 0x{Holder:X8} applied {Effect} to {Hit} target(s) within {Radius}m.", buff.BuffId, holder.Handle,
						area.EffectSpellId, hit, area.RadiusMetres);
				}

				// ⭐ #786: a `CastNano` chain edge can state a cadence too, and 263 of them do. It is NOT an area - it
				// re-runs on the HOLDER, with no radius - so it shares the tick and the `_auraTicks` stamp (the key
				// carries the row's own Sequence, so the two kinds cannot collide) but not `ApplyAreaEffect`.
				//
				// ⚠️ Only the `Repeats == -1` half is here. The other 220 interval-carrying edges state `Repeats == 1`,
				// which runs ONCE after a delay, and a delay needs a one-shot timer this tick does not have; running
				// them here would turn a single delayed cast into a permanent cadence, which is the over-apply #784
				// refused to guess at.
				foreach (var edge in await _spellAreas.RepeatingChains(buff.BuffId >> 8))
				{
					var key = (holder.Handle, buff.BuffId, edge.Sequence);
					if (_auraTicks.TryGetValue(key, out var lastChain) && (now - lastChain).TotalMilliseconds < edge.PeriodCentiseconds * 10)
						continue;

					_auraTicks[key] = now;

					// The same re-apply guard as every other landing: a holder still carrying the chained effect keeps
					// the live one counting down rather than re-asserting it, which is retail's own "re-apply only
					// after the previous has ended" (#274). So the cadence is a REFRESH schedule, not a stack.
					await ApplyEffectToEntity(client, holder.Handle, buff.CasterHandle, edge.EffectSpellId);
					_logger.LogDebug("Chain from buff 0x{Buff:X8} on 0x{Holder:X8} re-ran {Effect} (every {Period} cs).", buff.BuffId, holder.Handle,
						edge.EffectSpellId, edge.PeriodCentiseconds);
				}
			}
		}

		// #776: apply one of a spell's AREA elements WHOLE - the damage to everything in range, and then the effect
		// itself on each of them. `ApplySpellArea` used to be the entire operation and it only ever dealt the damage,
		// which is why `Gangrenous Stench` hit for its initial number and then did nothing: the 9.5 s effect record
		// the area names never landed on anybody, so it had no DoT to bleed, no stat deltas to apply, and its own
		// 4 m/3 s area was never reached.
		//
		// `sourceHandle` is what the area radiates from (the buff's holder); `casterHandle` is who gets the credit
		// and is the player for the whole chain, even where the emitter is an infected mob - a bleed that finishes a
		// mob has to name a killer the XP/loot lifecycle can pay.
		public async Task<int> ApplyAreaEffect(INetworkClient client, uint sourceHandle, uint casterHandle, SpellAreaEffect area)
		{
			var victims = await _output.ApplySpellArea(client, sourceHandle, area.EffectSpellId, area.RadiusMetres, area.MaxTargets);
			foreach (var handle in victims)
				await ApplyEffectToEntity(client, handle, casterHandle, area.EffectSpellId);

			return victims.Count;
		}

		// Land an effect spell on ONE non-player victim: the ApplySpell carrier so the client draws the icon on it,
		// and a tracked `ActiveBuff` so the tick can count it down, bleed its DoT and emit its own areas. The mirror
		// of what `ApplyNpcCast` does for a debuff an NPC puts on a player.
		private async Task ApplyEffectToEntity(INetworkClient client, uint victimHandle, uint casterHandle, uint effectSpellId, SpellChainWalk? chain = null)
		{
			if ((_players.TryGetValue(client, out var pe) ? pe : null) is not { } player || !_world.ZoneForEntity(player).TryGet(victimHandle, out var victim) || victim.IsPlayer || victim.IsDead)
				return;

			// Seed the #784 chain walk with the record landing here, so an edge pointing back at it terminates. Null at
			// an original landing (an area applying its effect); non-null only when this call IS a chain edge.
			chain ??= new SpellChainWalk();
			chain.TryEnter(effectSpellId);

			var spell = await _database.GetSpell(effectSpellId);
			var durationMs = spell?.DurationMs ?? 0;
			var wireId = effectSpellId << 8;

			// A zero duration is an INSTANT: retail sends no ApplySpell for one, and one aborts the client's
			// ActivateBuff (#767). It still LANDED, so its chain runs below - the icon is what is skipped, not the
			// effect.
			if (durationMs > 0)
			{
				// Already carrying it: leave the live one counting down rather than re-sending the apply. Re-applying on
				// every period is what STACKS icons on the client (#274) - retail re-applies an effect only after the
				// previous one has ended - and the area's damage lands either way.
				//
				// ⚠️ The chain is skipped with it, deliberately: nothing NEW landed on this victim, so re-running the
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

			// #784: the `CastNano` chain - "this landed, so also run X" on the SAME victim, with no radius of its own.
			// `Gangrenous Stench`'s 221069 reaching `Physical Wrack` 3963058 is the headline case, and the reason the
			// spell's tooltip effect did nothing until now. Every edge runs ONCE here; the ones that state a CADENCE
			// re-run from the aura tick instead (#786, `EmitAuraAreas`).
			foreach (var link in await _spellAreas.ChainedSpells(effectSpellId))
			{
				// #813: the target's STACKING LINE is claimed with its id - ten edges into one line is a rank table,
				// not ten buffs. Same rule as the player-side walk in BuffService.
				if (!chain.TryEnter(link.EffectSpellId, await _spellAreas.StackingLineOf(link.EffectSpellId)))
					continue;

				_logger.LogDebug("Effect {Effect} chains {Chained} onto {Npc} (0x{Handle:X8}).", effectSpellId, link.EffectSpellId, victim.Name, victim.Handle);
				await ApplyEffectToEntity(client, victim.Handle, casterHandle, link.EffectSpellId, chain);
			}

			if (chain.Truncated)
				_logger.LogWarning("Effect {Effect}'s chain hit the {Cap}-application cap on {Npc} and was truncated.", effectSpellId, SpellChainWalk.MaxApplications, victim.Name);
		}

		// #776: the world tick for buffs on NON-PLAYER entities. `TickBuffExpiry`, `TickDots` and `TickAuras` all walk
		// `_players`, so a debuff a player put on a MOB had nothing to expire it, nothing to bleed it and nothing to
		// emit its own areas - it counted down on the client to 0 and stuck there for ever, which is exactly the
		// orphan-icon failure #274 fixed on the player side.
		//
		// Everything here is attributed to `buff.CasterHandle`'s player: if they have gone (logged out, changed
		// playfield), the buff is dropped rather than left ticking, because there is no client left to render its end
		// or to be paid for a kill it lands.
		// Registers a non-player entity whose `ActiveBuffs` the tick below has to service. The only writer is the
		// effect-application path above; it is a separate method so the registry stays private and so a test can put
		// an entity in front of one tick pass without a database behind it.
		public void TrackBuffedEntity(WorldEntity entity)
		{
			_buffedEntities[entity.Handle] = entity;
		}

		public async Task TickNpcBuffs()
		{
			if (_buffedEntities.IsEmpty)
				return;

			var now = DateTimeOffset.UtcNow;
			foreach (var (handle, entity) in _buffedEntities.ToArray())
			{
				// Dead, despawned or clean: it leaves the registry. A corpse renders no icon, and a DoT that kept
				// ticking into a kill lifecycle that has already run would pay the XP twice.
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
							break; // the bleed landed the killing blow; the sweep above retires it next tick
					}
				}

				// The chain: an effect record can state an area of its OWN, so an infected mob spreads it to its
				// neighbours. Same op the player aura uses, centred on the mob.
				if (!entity.IsDead)
					await EmitAuraAreas(entity, null, now);
			}
		}

		// The connected player whose world entity carries `handle`, or null if nobody does (they logged out, or the
		// handle is a mob's). The reverse of PlayerWorldEntity, and the tick's way back to a client from a buff's
		// recorded caster.
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

					// .godmode has to cover DAMAGE OVER TIME, not just the swing that applied it. The two direct
					// NPC-damage paths check GodMode; this one did not, so a GM in god mode was killed by a bleed -
					// and by one the NPC cast path had applied THROUGH god mode, since it creates the buff before its
					// own GodMode return. The tick is the single choke point every DoT source funnels through, which
					// is why the guard belongs here rather than at each application site.
					//
					// The stamp above still advances, so a debuff that ticked through god mode does not burst-catch-up
					// the moment it is switched off. HEALS over time are unaffected: god mode blocks harm, not healing.
					if (entity.GodMode && buff.DotDelta < 0)
						continue;

					vitals.ApplyHealthDelta(buff.DotDelta);
					// #770: `SpellBaseId` is ZERO, like every other StatDamageIIR retail sends (see
					// CombatResolver.RetailSpellBaseId). This used to pass `buff.BuffId >> 8` - the DoT's own spell id -
					// which is why it survived the #767 fix to the cast path: that IS a real id, so the client loads it
					// without complaint and nothing ever looked wrong. It is simply not what retail states.
					await _output.SendStatDamage(client, entity.Handle, vitals.Health, buff.DotDelta, buff.DotType, buff.CasterHandle,
						buff.BuffId >> 8); // a HEALTH record names its real spell - see CombatResolver.RetailSpellBaseId
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
