using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The spell-effect dispatcher: given a spell, what does the SERVER have to do about it - beyond sending the
	// ApplySpell carrier and folding in its stat deltas?
	//
	// This replaces dispatching on hardcoded spell ids. A pet summon used to be a catalog lookup branch in the cast
	// resolve and the fatality proc a pair of constants FatalityService compared against; both
	// are now SpellEffect rows the importer seeds, so adding the next summon or proc is a row rather than a branch.
	//
	// What is deliberately NOT here, because it is not keyed by spell id and folding it in would be a fake
	// unification:
	//   - MOUNTS (the Mounts table, #627) are keyed by the client's MOUNT id - the SummonMount behavior's `from` word,
	//     which is the SUMMON spell's id, not the ridden spell's - and carry the mount creature the client animates
	//     from. A different axis; the wire constants around it stay in MountCatalog (docs/subsystems/mounts.md).
	//   - SPELLWEAVES (SpellweaveCatalog) are keyed by CLASS - the weave elements and the energy pool are per-class
	//     profiles, not per-spell effects.
	//   - Shield of the Risen and Exhausted are single named spell ids each used at ONE call site (world entry, energy
	//     depletion). Those are spell REFERENCES, not a dispatch table; a row would only add a lookup.
	public sealed class SpellEffectService
	{
		private readonly IDatabaseRepository _database;
		private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
		private readonly ILogger _logger;
		private readonly ISpellEffectOutbound _outbound;

		// (base spell id, trigger) -> its effects in Sequence order. Loaded once (a few dozen rows) on first use.
		private IReadOnlyDictionary<(uint SpellId, SpellEffectTrigger Trigger), SpellEffect[]>? _effects;

		public SpellEffectService(IDatabaseRepository database, ISpellEffectOutbound outbound, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
			_logger = logger;
		}

		// The effects a CAST carries. `spellBase` is the bare id from the cast trailer (not the wire (base << 8) form).
		public async Task<IReadOnlyList<SpellEffect>> ForCast(uint spellBase)
		{
			return await For(spellBase, SpellEffectTrigger.OnCast);
		}

		// The fatality-triggered effects armed by the buffs currently live on `entity` (#140). ActiveBuffs is keyed by
		// the WIRE id, so each is shifted back to its base to match a row. Returns them in (buff, sequence) order.
		public async Task<IReadOnlyList<SpellEffect>> ForFatality(WorldEntity entity)
		{
			var effects = await EnsureLoaded();
			if (effects.Count == 0)
				return [];

			var armed = new List<SpellEffect>();
			foreach (var buffId in entity.ActiveBuffs.Keys.OrderBy(k => k))
			{
				if (effects.TryGetValue((buffId >> 8, SpellEffectTrigger.OnFatality), out var rows))
					armed.AddRange(rows);
			}

			return armed;
		}

		// Runs one effect for the player. Returns whether it was actually carried out - a summon whose creature type is
		// missing reports false rather than pretending, so the caller can log the content gap (the buff that asked for
		// the pet is already on the player either way).
		public async Task<bool> Run(INetworkClient client, SpellEffect effect)
		{
			switch (effect.Kind)
			{
				case SpellEffectKind.SummonPet:
				{
					if (string.IsNullOrEmpty(effect.Template))
					{
						_logger.LogWarning("SpellEffect {Spell}/{Trigger}#{Seq} is a pet summon with no Template - nothing to spawn.", effect.SpellId, effect.Trigger, effect.Sequence);
						return false;
					}

					if (effect.DurationMs is not { } durationMs)
					{
						if (await _outbound.SummonPet(client, effect.Template, effect.Combat))
							return true;

						// The cast HAPPENED - the player paid for it and the client is waiting for a pet. Say which
						// creature type is missing rather than resolving the cast as if it did nothing.
						_logger.LogWarning("Spell {Spell} summons '{Template}', but no creature type by that name is spawnable (pet-creature import needed).", effect.SpellId, effect.Template);
						return false;
					}

					// A TIMED pet extends the one already out rather than stacking a second (the summon path owns that
					// rule); it has no "nothing spawned" answer to give back, so a missing type is reported there.
					await _outbound.SummonTimedPet(client, effect.Template, TimeSpan.FromMilliseconds(durationMs), effect.MaxDurationMs is { } cap
						? TimeSpan.FromMilliseconds(cap)
						: null);
					return true;
				}

				case SpellEffectKind.ApplySpell:
				{
					if (effect.TargetSpellId is not { } target)
					{
						_logger.LogWarning("SpellEffect {Spell}/{Trigger}#{Seq} applies a spell but names none.", effect.SpellId, effect.Trigger, effect.Sequence);
						return false;
					}

					await _outbound.ApplySelfBuff(client, target << 8);
					return true;
				}

				default:
				{
					throw new ArgumentOutOfRangeException(nameof(effect), effect.Kind, "Unhandled spell-effect kind.");
				}
			}
		}

		// Runs every effect of `effects` in order, and says whether there was anything to run - the cast resolve uses
		// that to decide between "this spell's effect IS the summon" and the generic buff-apply resolution.
		public async Task<bool> RunAll(INetworkClient client, IReadOnlyList<SpellEffect> effects)
		{
			foreach (var effect in effects)
				await Run(client, effect);

			return effects.Count > 0;
		}

		private async Task<IReadOnlyList<SpellEffect>> For(uint spellBase, SpellEffectTrigger trigger)
		{
			var effects = await EnsureLoaded();
			return effects.TryGetValue((spellBase, trigger), out var rows)
				? rows
				: [];
		}

		private async Task<IReadOnlyDictionary<(uint SpellId, SpellEffectTrigger Trigger), SpellEffect[]>> EnsureLoaded()
		{
			if (_effects != null)
				return _effects;

			await _loadLock.WaitAsync();
			try
			{
				if (_effects != null)
					return _effects;

				var rows = await _database.GetSpellEffects();
				_effects = rows.GroupBy(r => (r.SpellId, r.Trigger)).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Sequence).ToArray());

				// An EMPTY table is not a valid state - the seed carries these rows, so nothing to dispatch means a
				// database that predates the spell-effects module. Say so once at load rather than letting every pet
				// summon quietly resolve as a plain buff.
				if (rows.Length == 0)
					_logger.LogWarning("SpellEffectService: no spell effects in the database - pet summons and triggered procs will not fire. Run the importer's `spell-effects` module.");
				else
					_logger.LogInformation("SpellEffectService: {Rows} effect(s) over {Spells} spell(s).", rows.Length, _effects.Keys.Select(k => k.SpellId).Distinct().Count());

				return _effects;
			}
			finally
			{
				_loadLock.Release();
			}
		}
	}
}
