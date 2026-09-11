using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Combat;

namespace Faolan.Tests.Support
{
	// The spell-area / chain-edge catalog a tick test drives by hand. The world simulation reads the catalog directly
	// rather than through IWorldOutbound, so the seam is here. Seed `Auras` / `Chains` by spell id; anything unseeded
	// answers empty.
	//
	// `base(null!)` is deliberate: every lookup below is overridden and none calls base, so the database handle the
	// real catalog caches from is never touched.
	public sealed class RecordingSpellAreaCatalog() : SpellAreaCatalog(null!)
	{
		public Dictionary<uint, IReadOnlyList<SpellAreaEffect>> Auras { get; } = [];
		public Dictionary<uint, IReadOnlyList<SpellAreaEffect>> Chains { get; } = [];

		public override Task<IReadOnlyList<SpellAreaEffect>> AuraAreas(uint spellId)
		{
			return Task.FromResult(Auras.TryGetValue(spellId, out var areas)
				? areas
				: []);
		}

		public override Task<IReadOnlyList<SpellAreaEffect>> ChainedSpells(uint spellId)
		{
			return Task.FromResult(Chains.TryGetValue(spellId, out var links)
				? links
				: []);
		}

		// The cadence half of the same map - filtered here rather than held in a second dictionary, so a test
		// that seeds `Chains` cannot disagree with itself about which of them repeat.
		public override Task<IReadOnlyList<SpellAreaEffect>> RepeatingChains(uint spellId)
		{
			return Task.FromResult(Chains.TryGetValue(spellId, out var links)
				? (IReadOnlyList<SpellAreaEffect>)links.Where(l => l.PeriodCentiseconds > 0 && l.Repeats == -1).ToList()
				: []);
		}

		// No stacking line in the double - these tests drive the chain by id, and a real line map would need the
		// spell catalog behind it. A 0 means "in no line", which is the non-interfering answer.
		public override Task<uint> StackingLineOf(uint spellId)
		{
			return Task.FromResult(0u);
		}
	}
}
