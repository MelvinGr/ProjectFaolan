using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	// A purpose-built editor for a spell and its effects, next to the generic DbEditor and the QuestEditor.
	//
	// Why not just use DbEditor: a spell's behaviour is spread over THREE tables, and the interesting one is a handful
	// of rows hiding in a table of thousands. Wiring a summon or a proc meant finding the parent in ~20k Spells rows,
	// then finding its children in SpellEffects by hand, with the row cap in the way and no hint which columns the
	// chosen Kind actually reads. Here a spell shows its own fields, its server-acted effects and its extracted stat
	// effects on one screen, and the effect columns disable themselves for the kinds that ignore them.
	//
	// SpellEffects is EDITABLE (a few dozen hand-decoded rows - the pet summons and the fatality proc). SpellStatEffects
	// is READ-ONLY on purpose: it is the client's own [2][9] template region, re-derived wholesale on every RDB
	// re-import, so an edit here would be silently discarded by the next importer run. It is shown because it is what a
	// buff actually modifies.
	//
	// Admin-gated, edits the live DB (restart to apply) - same contract as the other two editors.
	public partial class SpellEditor : IDisposable
	{
		private static readonly SpellEffectTrigger[] Triggers = Enum.GetValues<SpellEffectTrigger>();
		private static readonly SpellEffectKind[] Kinds = Enum.GetValues<SpellEffectKind>();

		// The catalog is ~20k rows, so the list is filtered down before rendering rather than paged: rendering every
		// row as a button is what would make this page unusable, not loading them.
		private const int ShowCap = 300;
		private DatabaseContext? _ctx;
		private List<Spell> _spells = [];

		// Effects by spell id, loaded once for the whole catalog: they are a few dozen / ~1700 rows, cheap to hold, and
		// having them up front is what lets the list show a badge per spell and the filter select "only with effects".
		private Dictionary<uint, List<SpellEffect>> _effects = [];
		private Dictionary<uint, List<SpellStatEffect>> _statEffects = [];
		private Spell? _spell;
		private bool _authorized;
		private bool _error;
		private bool _onlyWithEffects;
		private string _filter = "";
		private string? _status;
		private int _shown;

		[CascadingParameter]
		private Task<AuthenticationState>? AuthState { get; set; }

		public void Dispose()
		{
			_ctx?.Dispose();
		}

		protected override async Task OnInitializedAsync()
		{
			var user = AuthState == null
				? null
				: (await AuthState).User;
			if (user == null || !user.IsInRole("Admin"))
			{
				Nav.NavigateTo("/", true);
				return;
			}

			_authorized = true;
			_ctx = await DbFactory.CreateDbContextAsync();
			_spells = await _ctx.Spells.OrderBy(s => s.Id).ToListAsync();
			_effects = (await _ctx.SpellEffects.ToListAsync()).GroupBy(e => e.SpellId).ToDictionary(g => g.Key, g => g.ToList());
			_statEffects = (await _ctx.SpellStatEffects.ToListAsync()).GroupBy(e => e.SpellId).ToDictionary(g => g.Key, g => g.ToList());

			// Open on something worth looking at rather than spell #1: the spells that HAVE server-acted effects are the
			// reason this page exists.
			var first = _spells.FirstOrDefault(s => _effects.ContainsKey(s.Id)) ?? _spells.FirstOrDefault();
			if (first != null)
				Select(first);
		}

		private void AddEffect()
		{
			if (_spell == null || _ctx == null)
				return;

			var list = _effects.TryGetValue(_spell.Id, out var existing)
				? existing
				: _effects[_spell.Id] = [];

			// The key is (SpellId, Trigger, Sequence), so a new row has to land on a free sequence for its trigger or
			// SaveChanges fails on a duplicate key.
			const SpellEffectTrigger trigger = SpellEffectTrigger.OnCast;
			var nextSeq = list.Where(e => e.Trigger == trigger).Select(e => (int)e.Sequence).DefaultIfEmpty(-1).Max() + 1;
			var fx = new SpellEffect
			{
				SpellId = _spell.Id,
				Trigger = trigger,
				Sequence = (byte)nextSeq,
				Kind = SpellEffectKind.SummonPet
			};
			list.Add(fx);
			_ctx.SpellEffects.Add(fx);
			Set("Added effect (unsaved — press Save)");
		}

		private int EffectCount(uint spellId)
		{
			return _effects.TryGetValue(spellId, out var l)
				? l.Count
				: 0;
		}

		private IEnumerable<SpellEffect> Effects()
		{
			return _spell != null && _effects.TryGetValue(_spell.Id, out var l)
				? l.OrderBy(e => e.Trigger).ThenBy(e => e.Sequence)
				: [];
		}

		private IEnumerable<Spell> Filtered()
		{
			IEnumerable<Spell> q = _spells;
			if (_onlyWithEffects)
				q = q.Where(s => _effects.ContainsKey(s.Id) || _statEffects.ContainsKey(s.Id));

			if (!string.IsNullOrWhiteSpace(_filter))
			{
				var f = _filter.Trim();
				q = q.Where(s => s.Id.ToString().Contains(f, StringComparison.OrdinalIgnoreCase) || (s.Name ?? "").Contains(f, StringComparison.OrdinalIgnoreCase));
			}

			var list = q.Take(ShowCap).ToList();
			_shown = list.Count;
			return list;
		}

		private void RemoveEffect(SpellEffect fx)
		{
			if (_ctx == null || _spell == null)
				return;

			if (_effects.TryGetValue(_spell.Id, out var list))
				list.Remove(fx);
			// A never-saved row is only tracked as Added, so dropping it is enough; a persisted one must be explicitly
			// removed for SaveChanges to delete it.
			if (_ctx.Entry(fx).State != EntityState.Added)
				_ctx.Remove(fx);
			Set("Removed effect (unsaved — press Save)");
		}

		private async Task Save()
		{
			if (_ctx == null)
				return;

			try
			{
				var n = await _ctx.SaveChangesAsync();
				Set($"Saved — {n} change(s) written. Restart the server to apply.");
			}
			catch (Exception ex)
			{
				// The CHECK constraints (ConstrainToEnumValues on Trigger/Kind) and the composite key surface here, so
				// show the inner message: "UNIQUE constraint failed" is the useful half, not the EF wrapper.
				Set(ex.InnerException?.Message ?? ex.Message, true);
			}
		}

		private void Select(Spell s)
		{
			_spell = s;
		}

		private void Set(string msg, bool err = false)
		{
			_status = msg;
			_error = err;
		}

		private IEnumerable<SpellStatEffect> StatEffects()
		{
			return _spell != null && _statEffects.TryGetValue(_spell.Id, out var l)
				? l.OrderBy(e => e.StatId)
				: [];
		}

		private int StatEffectCount(uint spellId)
		{
			return _statEffects.TryGetValue(spellId, out var l)
				? l.Count
				: 0;
		}
	}
}
