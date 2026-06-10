using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	public class CharacterEquipment : BaseEntity
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		public virtual Item? Belt { get; set; }
		public virtual Item? Boots { get; set; }
		public virtual Item? Chest { get; set; }
		public virtual Item? Cloak { get; set; }
		public virtual Item? FirstPosBag { get; set; }
		public virtual Item? Hands { get; set; }
		public virtual Item? Head { get; set; }
		public virtual Item? Legs { get; set; }
		public virtual Item? MainHandLeft { get; set; }
		public virtual Item? MainHandRight { get; set; }
		public virtual Item? Neck { get; set; }
		public virtual Item? OffHandLeft { get; set; }
		public virtual Item? OffHandRight { get; set; }
		public virtual Item? RingLeft { get; set; }
		public virtual Item? RingRight { get; set; }
		public virtual Item? Shoulder { get; set; }
		public virtual Item? Tokens { get; set; }
		public virtual Item? Wrist { get; set; }

		// Capture analysis note:
		// the large 0x737A6DF9 initial-state packet currently looks like a nested chain
		// 101 -> 104 -> 112 -> 113, where 104 is the strongest normal bag candidate.
		// The remaining two serialized string fields below therefore likely correspond to
		// the other non-equipment blocks (tentatively Resources and Quest), but that mapping
		// is still evidence-based rather than protocol-confirmed.
		[Obsolete]
		public string Bag { get; set; }

		[Obsolete]
		public string Quest { get; set; }

		[Obsolete]
		public string Resources { get; set; }

		public uint CharacterId { get; set; }
	}

	/*public class CharacterAbility : BaseObject
	{
	    public string Name { get; set; }

	    public uint? Level { get; set; }

	    public uint? Data0 { get; set; }

	    public uint? Data1 { get; set; }

	    public uint? Data2 { get; set; }

	    public uint? Data3 { get; set; }

	    public uint? Data4 { get; set; }
	}*/
}
