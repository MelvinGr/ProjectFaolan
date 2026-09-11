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
	/// <summary>
	///     The spell-effect dispatcher: given a spell, what the server has to do about it beyond sending the ApplySpell
	///     carrier and folding in its stat deltas.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Effects are <c>SpellEffect</c> rows the importer seeds rather than branches on hardcoded spell ids, so
	///         the next summon or proc is a row.
	///     </para>
	///     <para>
	///         Three neighbouring mechanics are deliberately not here, because they are not keyed by spell id and
	///         folding them in would be a fake unification. Mounts (the <c>Mounts</c> table) are keyed by the client's
	///         mount id - the SummonMount behavior's <c>from</c> word, which is the summon spell's id, not the ridden
	///         spell's - and carry the mount creature the client animates from; the wire constants around them stay in
	///         <c>MountCatalog</c> (docs/subsystems/mounts.md). Spellweaves (<c>SpellweaveCatalog</c>) are keyed by
	///         class, the weave elements and energy pool being per-class profiles. Shield of the Risen and Exhausted
	///         are single named spell ids used at one call site each - world entry and energy depletion - which makes
	///         them spell references rather than a dispatch table.
	///     </para>
	/// </remarks>
	public sealed class SpellEffectService
	{
		/// <summary>
		///     Builds the triggered-effect table at startup rather than on the first pet summon or proc - see
		///     <see cref="BuffStatEffects.WarmAsync" />.
		/// </summary>
		public async Task WarmAsync() => await EnsureLoaded();

		private readonly ICatalogRepository _database;
		private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
		private readonly ILogger _logger;
		private readonly ISpellEffectOutbound _outbound;

		// (base spell id, trigger) -> its effects in Sequence order. Loaded once (a few dozen rows) on first use.
		private IReadOnlyDictionary<(uint SpellId, SpellEffectTrigger Trigger), SpellEffect[]>? _effects;

		public SpellEffectService(ICatalogRepository database, ISpellEffectOutbound outbound, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
			_logger = logger;
		}

		/// <summary>
		///     The effects a cast carries. <paramref name="spellBase" /> is the bare id from the cast trailer, not the
		///     wire (base &lt;&lt; 8) form.
		/// </summary>
		public async Task<IReadOnlyList<SpellEffect>> ForCast(uint spellBase)
		{
			return await For(spellBase, SpellEffectTrigger.OnCast);
		}

		/// <summary>
		///     The fatality-triggered effects armed by the buffs currently live on <paramref name="entity" />, in
		///     (buff, sequence) order.
		/// </summary>
		/// <remarks>
		///     <c>ActiveBuffs</c> is keyed by the wire id, so each is shifted back to its base to match a row.
		/// </remarks>
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

		/// <summary>
		///     Runs one effect for the player.
		/// </summary>
		/// <returns>
		///     Whether it was actually carried out: a summon whose creature type is missing reports false rather than
		///     pretending, so the caller can log the content gap - the buff that asked for the pet is already on the
		///     player either way.
		/// </returns>
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

					// The pet's point cost comes from the spell that summoned it and goes on the wire in the PetIIR
					// assign, because the client counts its pet-point bar from there and the server states it nowhere
					// else. Read from the spell rather than joined by creature name: several spells can name one
					// creature, and a name join makes `Demon Avenger` read 1 where retail assigns 4.
					var pointCost = (await _database.GetSpell(effect.SpellId))?.SummonPointCost ?? 0;

					if (effect.DurationMs is not { } durationMs)
					{
						if (await _outbound.SummonPet(client, effect.Template, effect.Combat, pointCost))
							return true;

						// The cast happened - the player paid for it and the client is waiting for a pet. Say which
						// creature type is missing rather than resolving the cast as if it did nothing.
						_logger.LogWarning("Spell {Spell} summons '{Template}', but no creature type by that name is spawnable (pet-creature import needed).", effect.SpellId, effect.Template);
						return false;
					}

					// A timed pet extends the one already out rather than stacking a second - the summon path owns that
					// rule - and has no "nothing spawned" answer to give back, so a missing type is reported there.
					await _outbound.SummonTimedPet(client, effect.Template, TimeSpan.FromMilliseconds(durationMs), effect.MaxDurationMs is { } cap
						? TimeSpan.FromMilliseconds(cap)
						: null, pointCost);
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

		/// <summary>
		///     Runs every effect in order.
		/// </summary>
		/// <returns>
		///     Whether there was anything to run. The cast resolve uses that to decide between "this spell's effect is
		///     the summon" and the generic buff-apply resolution.
		/// </returns>
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

				var clock = System.Diagnostics.Stopwatch.StartNew();
				var rows = await _database.GetSpellEffects();
				_effects = rows.GroupBy(r => (r.SpellId, r.Trigger)).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Sequence).ToArray());

				// An empty table is not a valid state - the seed carries these rows, so nothing to dispatch means a
				// database that predates the spell-effects module. Say so once at load rather than letting every pet
				// summon quietly resolve as a plain buff.
				if (rows.Length == 0)
					_logger.LogWarning("SpellEffectService: no spell effects in the database - pet summons and triggered procs will not fire. Run the importer's `spell-effects` module.");
				else
					_logger.LogInformation("SpellEffectService: {Rows} effect(s) over {Spells} spell(s). Built in {Ms} ms.", rows.Length, _effects.Keys.Select(k => k.SpellId).Distinct().Count(), clock.ElapsedMilliseconds);

				return _effects;
			}
			finally
			{
				_loadLock.Release();
			}
		}
	}
}
