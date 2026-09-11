using System;
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
	///     The feat proc engine: a trained feat hangs an extra spell off a combo or a spell line, to fire on a trigger -
	///     "Touch of Death: empowers their next combo with additional unholy damage", "Holy Vengeance: after casting
	///     the Wave of Life the priest is infused...".
	/// </summary>
	/// <remarks>
	///     A character's procs are resolved once at world entry into <c>WorldEntity.Procs</c> - the same shape as
	///     <c>ComboBonuses</c> and <c>SpellBoosts</c>, and for the same reason, that a swing must not turn into a query
	///     - so an untrain that reloads the entity drops them with the feat. The two hooks are the two places the
	///     trigger events happen: <see cref="ComboFinisher" /> when a combo lands, <see cref="CastService" /> when a
	///     cast completes. What fires is an ordinary ApplySpell: the proc spell on the carrier (key 88) and/or on the
	///     target (key 236), at rank 0 like a cast.
	/// </remarks>
	public sealed class ProcService
	{
		private readonly BuffService _buffs;
		private readonly ICatalogRepository _catalog;
		private readonly ILogger _logger;
		private readonly Func<double> _roll;
		private readonly WorldSimulation _sim;

		public ProcService(WorldSimulation sim, ICatalogRepository catalog, BuffService buffs, ILogger logger)
			: this(sim, catalog, buffs, logger, () => Random.Shared.NextDouble())
		{
		}

		internal ProcService(WorldSimulation sim, ICatalogRepository catalog, BuffService buffs, ILogger logger, Func<double> roll)
		{
			_sim = sim;
			_catalog = catalog;
			_buffs = buffs;
			_logger = logger;
			_roll = roll;
		}

		/// <summary>
		///     A combo landed on <paramref name="targetHandle" />, which is 0 for an air swing. The event's length is
		///     the combo's own animation time.
		/// </summary>
		public async Task OnComboLanded(INetworkClient client, ComboSwing swing, uint targetHandle)
		{
			ArgumentNullException.ThrowIfNull(swing);
			var caster = _sim.PlayerWorldEntity(client);
			if (caster == null || caster.Procs.Count == 0)
				return;

			foreach (var proc in ProcRules.ForCombo(caster.Procs, swing.Combo.Id))
				await Fire(client, proc, client.Session.Character.Id, targetHandle, swing.AnimationSeconds, $"combo '{swing.Combo.Name}'");
		}

		/// <summary>
		///     A cast of <paramref name="spellId" /> completed, <paramref name="offensive" /> when it landed on a
		///     hostile target. The event's length is the spell's cast time.
		/// </summary>
		public async Task OnSpellCast(INetworkClient client, uint spellId, uint targetHandle, bool offensive)
		{
			var caster = _sim.PlayerWorldEntity(client);
			if (caster == null || caster.Procs.Count == 0)
				return;

			var spell = await _catalog.GetSpell(spellId);
			if (spell == null || spell.GuiStackingLine == 0)
				return;

			foreach (var proc in ProcRules.ForSpellLine(caster.Procs, spell.GuiStackingLine, offensive))
				await Fire(client, proc, client.Session.Character.Id, offensive ? targetHandle : 0, spell.CastTime / 1000.0, $"spell '{spell.Name}'");
		}

		private async Task Fire(INetworkClient client, FeatProc proc, uint casterHandle, uint targetHandle, double eventSeconds, string source)
		{
			var chance = ProcRules.Chance(proc.Amount, eventSeconds);
			if (_roll() >= chance)
				return;

			// ApplySpell takes the wire id ((base << 8) | rank); the table carries the bare base, as the cast path does.
			if (proc.SelfSpellId is { } self)
				await _buffs.ApplySpell(client, casterHandle, casterHandle, self << 8);

			if (proc.TargetSpellId is { } target && targetHandle != 0 && targetHandle != casterHandle)
				await _buffs.ApplySpell(client, targetHandle, casterHandle, target << 8);

			_logger.LogInformation("Proc: feat {Feat} fired on {Source} ({Trigger}, {Rate}/min over {Seconds:0.0}s = {Chance:P0}): self {Self}, target {Target}.",
				proc.FeatId, source, (FeatProcTrigger)proc.Trigger, proc.Amount, eventSeconds, chance, proc.SelfSpellId, proc.TargetSpellId);
		}
	}
}
