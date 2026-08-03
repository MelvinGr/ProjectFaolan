using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     A spell a feat GRANTS (#593) - "Spell Unlocking 60-80 teaches Blood Pact, Covenant of Vengeance and Drain
	///     Essence ranks 1-4". Not a modifier: nothing about an existing spell changes, the character simply gains one.
	/// </summary>
	/// <remarks>
	///     GameAction kind 53019 (<c>Spell</c>), whose key list is a single slot - one spell template id and nothing
	///     else. That one-word shape is the whole reason it looked undecodable for a while (#591): read as the
	///     <c>(reference, magnitude)</c> pair every OTHER kind carries, its "magnitude" was the next element's type
	///     word, which is why the value appeared to repeat the kind id.
	///     <para>
	///         Kept apart from <see cref="FeatSpellStatModifier" /> deliberately. Filing a grant as a modifier would
	///         record a spell the feat GIVES you as a boost to one you already have - the trap #591 named, and the
	///         reason these kinds stayed out of the DB until each was decoded on its own evidence. All 1,749 records
	///         resolve to a shipped <see cref="Spell.Id" />.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(FeatId), nameof(Sequence))]
	public class FeatSpellGrant
	{
		public uint FeatId { get; set; }

		[ForeignKey(nameof(FeatId))]
		public virtual Feat Feat { get; set; } = null!;

		/// <summary>Wire position within the feat's modifier list, so the list regenerates in the client's order.</summary>
		public int Sequence { get; set; }

		/// <summary>The spell template the feat teaches - <see cref="Spell.Id" />, a template id and not a line.</summary>
		public uint SpellId { get; set; }
	}
}
