namespace Faolan.Core.Database.Tables
{
	// A spell a class is granted at character creation, harvested from the create-flow captures (the DispatchLearnSpell
	// 0x200A grants a fresh caster receives on the beach - e.g. Necromancer 0x125D9B/0x42CA77, Herald of Xotli
	// 0x3006FF). The spell analogue of Ability.IsStartingAbility/Class (#74): the fresh-character grant + action-bar
	// seed read it via GetStartingSpells(class, level). Keyed by (Class, SpellId). Class 0 = generic (granted to every
	// class); a non-zero value = class-specific. Coverage is only as complete as our create captures (casters we have a
	// beach capture for); physical classes have no L1 spells, which is correct.
	public class ClassStartingSpell : BaseEntity
	{
		public uint Class { get; set; }
		public uint Level { get; set; }

		// FK into the Spell catalog. The importer ensures a catalog row exists for every harvested starting spell, so
		// this navigation always resolves (load it with .Include(s => s.Spell) when the spell's params are needed).
		// virtual so the lazy-loading-proxy DbContext used in tests can build its model (every navigation must be
		// virtual; the live app doesn't enable proxies).
		public uint SpellId { get; set; }
		public virtual Spell Spell { get; set; }
	}
}
