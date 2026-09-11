using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// An ability a character has been granted (one row per character+ability) - the ability twin of CharacterSpell.
	// Abilities are not carried in the snapshot known-list (that is spells only), so this is the authoritative per-
	// character ability set: retail re-grants a character's whole ability kit on every login via DispatchAddSpecialAbility
	// (s2c 0x2052), so GrantCharacterAbilities replays these rows on world entry. The ability definition (Slot/Data
	// identity, name by id in the client RDB) lives in the Abilities catalog; this just records which abilities a
	// character knows. Captured characters are seeded from their observed 0x2052 grants (CaptureAbilityImporter, attributed
	// via the grant's RpcHeader receiver = the character id) - so the action bar is pure shortcuts, not the grant source.
	[PrimaryKey(nameof(CharacterId), nameof(AbilityId))]
	public class CharacterAbility
	{
		// The ability id (Abilities.Id); the client resolves the ability's name/icon from this.
		public uint AbilityId { get; set; }

		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;
	}
}
