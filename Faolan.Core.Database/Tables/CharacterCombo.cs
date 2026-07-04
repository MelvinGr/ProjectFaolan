using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A combo a character's combo book contains (one row per character+combo) - the combo twin of CharacterAbility.
	// Combos are class/level content delivered via ComboBookIIR (s2c 0xA8BBEB7F): retail sends a character's combos as
	// its combo book gains them (per level, LevelComboCatalog) and on the create-flow. A MAX-LEVEL imported character
	// never dings, so without a persisted set it would have an EMPTY combo book on login. This records WHICH combos a
	// character has so GrantCharacterCombos can re-emit them on world entry. Captured characters are seeded from their
	// observed ComboBookIIR grants (CaptureAbilityImporter, attributed via the grant's RpcHeader receiver = character id).
	[PrimaryKey(nameof(CharacterId), nameof(ComboId))]
	public class CharacterCombo
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		public uint CharacterId { get; set; }

		// The combo id (e.g. 0x00416544); the client resolves the combo from this via ComboBookIIR.
		public uint ComboId { get; set; }
	}
}
