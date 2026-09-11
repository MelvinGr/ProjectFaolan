using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     Value factories for <c>KnownSpellList</c> - what production builds one from, kept off the codec's surface,
	///     since choosing between semantic forms by signature would be ambiguous.
	/// </summary>
	public static class KnownSpellLists
	{
		// A character's learned spells, all type 2. A null or empty input yields the empty list, which frames as the bare
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
