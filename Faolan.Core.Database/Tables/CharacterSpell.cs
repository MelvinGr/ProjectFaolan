using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A spell a character has learned (one row per character+spell). The spell DEFINITION (name/effect/cost) lives in
	// the Spells catalog + the client RDB (rendered by id); this just records WHICH spells a character knows. Retail
	// re-grants the whole learned set on every login via DispatchLearnSpell (s2c 0x200A, body = [u32 spellId]) - so
	// GrantCharacterSpells replays these rows on world entry, the spell counterpart to GrantCharacterAbilities. The
	// grant packet was decoded in #73; #74 actually grants the spells. Captured characters are seeded with their
	// observed learned set (CaptureSpellImporter, attributed via the grant's RpcHeader receiver = the character id).
	[PrimaryKey(nameof(CharacterId), nameof(SpellId))]
	public class CharacterSpell
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		public uint CharacterId { get; set; }

		// The RDB spell id (e.g. 0x4213CD); the client resolves the spell's name/icon/effect from this.
		public uint SpellId { get; set; }
	}
}
