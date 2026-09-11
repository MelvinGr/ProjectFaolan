using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One slot of the character's FullChar-snapshot stat sheet (the 0x737A6DF9 tails stat block): an ordered
	// [statId][value] pair. The whole sheet - StatList#1 (the 17 displayed stats) then StatList#2 (the 74 sheet
	// stats) - is stored per character and authored into the snapshot from these rows, replacing the fixed fresh-char
	// skeleton + targeted patches. Ordinal preserves wire order (statIds repeat across/within the lists, so it is the
	// key, not StatId). Imported from the character's captured login snapshot (CaptureCharacterImporter); a freshly
	// created character is seeded with the class default. Unnamed stat ids are surfaced as Unknown{ordinal} in tooling.
	[PrimaryKey(nameof(CharacterId), nameof(Ordinal))]
	public class CharacterStat
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		public CharacterStatId StatId { get; set; }

		// Position in the stat block: 0..16 = StatList#1 (displayed), 17.. = StatList#2 (sheet). Preserves order and
		// disambiguates repeated stat ids.
		public int Ordinal { get; set; }

		public uint Value { get; set; }
	}
}
