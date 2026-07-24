using System.ComponentModel.DataAnnotations;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	// An ability/skill catalog entry (keyed by its RDB id). Key is the 128-bit TEA key (4 words) for its RDB 1000020
	// template (the source of Name, via RdbAbilityImporter); Data0 is a separate leading identity word from the 0x2052
	// grant, NOT part of the key. WHICH classes have the ability, and at WHAT level, live in ClassAbility (the
	// class<->ability access map) - a true multi-class membership derived from the per-character grants - NOT here
	// (the ability template carries no class/level requirement). Replaces the old IsStartingAbility + Class/Level columns.
	public class Ability : BaseEntity
	{
		// The ability template's 128-bit TEA DecryptionKey (owned -> flat columns Key1..Key4). virtual for the
		// lazy-loading-proxy test context (owned types are eager; virtual only satisfies the all-navigations-virtual rule).
		public virtual ProtoDecryptionKey Key { get; set; } = new();

		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// The leading identity word of the 0x2052 grant (written before the key). Distinct from Key; meaning not fully decoded.
		public uint Data0 { get; set; }

		// The ability's wire "slot"/template word: the leading u32 of the 0x2052 ability-grant packet (verified against
		// the strickenbla combat capture: 769094->108, 769099->112). Despite the "slot" name it is ability-intrinsic
		// metadata, not a hotbar position.
		public uint Slot { get; set; }

		// True when this row carries everything a 0x2052 grant needs. FALSE for the identity-only rows the client's own
		// data yields (RdbLdbAbilityImporter, #303): the LDB names all 265 abilities, but the 128-bit TEA key rides the
		// GRANT, and the keys are random - so an ability no capture ever granted is a name and an id, nothing more.
		// Granting one would put a null key on the wire, so the grant path skips it instead.
		public bool IsGrantable => Key.Key1 != 0 || Key.Key2 != 0 || Key.Key3 != 0 || Key.Key4 != 0;
	}
}
