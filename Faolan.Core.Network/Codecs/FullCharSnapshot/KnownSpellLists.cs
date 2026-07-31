using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// Value factories for KnownSpellList - what production builds one FROM, kept off the codec's surface (#530
	// rule 1: a codec exposes Build and Parse and nothing else, and choosing between semantic forms by signature is
	// exactly the ambiguity that removes).
	public static class KnownSpellLists
	{
		// A character's learned spells, all type 2. Null/empty yields the empty list, which frames as the bare 0x3F1
		// count word - byte-identical to a fresh character's captured tails.
		public static KnownSpellList FromSpellIds(IReadOnlyList<uint>? spellIds)
		{
			return new KnownSpellList
			{
				Records = [.. (spellIds ?? []).Select(id => new KnownSpellRecord(id, KnownSpellListCodec.SpellType))]
			};
		}
	}
}
