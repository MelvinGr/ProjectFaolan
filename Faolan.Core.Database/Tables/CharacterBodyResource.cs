using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One appearance resource the character chose in the create editor: the editor reports them as
	// SetMorphValue callbacks whose value is a resource ref ("*hairmesh_208096", "*texscript_1108153" for
	// hair/eye/skin color, tattoos, scars). Stored in arrival order (= the retail wire order of the spawn
	// scaffold's weighted entries); resolved to entry-table records via [[MorphTarget]] at world entry.
	public class CharacterBodyResource : BaseEntity
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// Arrival order in the editor stream (the weighted entries' wire order).
		public int Sequence { get; set; }

		// The resource ref as sent by the editor, e.g. "*texscript_1108153".
		[MaxLength(128)]
		public string Ref { get; set; } = null!;
	}
}
