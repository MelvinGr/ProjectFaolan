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

		// The ability's ACTION NUMBER: the leading u32 of its 0x2052 grant, and the word the client sends back in
		// `PerformSpecialAction` (0x205A) when the player triggers it. Not a hotbar position, despite the name.
		//
		// The two halves were established from opposite directions and meet exactly (#505): this column was verified
		// against the strickenbla combat capture as 769094->108 and 769099->112, and 0x06C/0x070 are the two
		// `PerformSpecialAction` selectors the melee combo inputs send - which are `Upper Left Attack` and
		// `Overhead Attack`. Every selector the corpus contains resolves against this column, with no near-miss:
		// 0x064 Search, 0x068 Hide, 0x06A Rest, 0x0FF the recipe book, 0x103 Switch Weapons, 0x105-0x109 the pet
		// commands, 0x4E2 Toggle Combat, 0x4E4 Crouch, 0x4E6 Dash (docs/subsystems/player-actions.md).
		//
		// The consequence is worth stating where it will be read: **the client only has the number if a grant gave it
		// the number**, so an ability that was never granted is a button that emits NOTHING - not a packet the server
		// ignores. That is the first thing to check when a stance does nothing in-client.
		public uint Slot { get; set; }

		// True when this row carries everything a 0x2052 grant needs. FALSE for the identity-only rows the client's own
		// data yields (RdbLdbAbilityImporter, #303): the LDB names all 265 abilities, but the 128-bit TEA key rides the
		// GRANT, and the keys are random - so an ability no capture ever granted is a name and an id, nothing more.
		// Granting one would put a null key on the wire, so the grant path skips it instead.
		public bool IsGrantable => Key.Key1 != 0 || Key.Key2 != 0 || Key.Key3 != 0 || Key.Key4 != 0;
	}
}
