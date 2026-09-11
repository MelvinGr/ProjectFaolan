using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One appearance resource the character chose in the create editor. The editor reports them as SetMorphValue
	///     callbacks whose value is a resource ref ("*hairmesh_208096", "*texscript_1108153" for hair/eye/skin color,
	///     tattoos, scars).
	/// </summary>
	/// <remarks>
	///     Rows are stored in arrival order, which is the wire order of the spawn scaffold's weighted entries, and are
	///     resolved to entry-table records through <see cref="MorphTarget" /> at world entry.
	/// </remarks>
	public class CharacterBodyResource : BaseEntity
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		/// <summary>Arrival order in the editor stream, which is the weighted entries' wire order.</summary>
		public int Sequence { get; set; }

		/// <summary>The resource ref as sent by the editor, e.g. "*texscript_1108153".</summary>
		[MaxLength(128)]
		public string Ref { get; set; } = null!;
	}
}
