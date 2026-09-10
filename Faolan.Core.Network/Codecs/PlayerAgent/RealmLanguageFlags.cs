using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     The <c>Dimension_t+0x24</c> bitfield a realm advertises - what the client resolves a realm's language from
	///     (#933).
	/// </summary>
	/// <remarks>
	///     ⭐ Recovered from <c>FUN_00A69180</c>, which turns this word into the client's own language id:
	///     <code>
	///     mov  0x24(%eax),%eax
	///     test $0x1,%al        -> 0  (English)
	///     test $0x2,%al        -> 0  (English)
	///     test $0x400,%eax     -> 5  (Russian)
	///     test $0x800,%eax     -> 4  (Polish)
	///     and  $0x100000,%eax  -> 6  (Korean), else 0
	///     </code>
	///     <para />
	///     ⭐ French, Spanish and German have NO bit of their own, and that is the finding rather than a gap in it.
	///     The mapper folds them into its English answer, and the widget row this pairs with holds exactly four
	///     children - <c>EnglishFlag</c>, <c>GermanFlag</c>, <c>FrenchFlag</c>, <c>SpanishFlag</c> - shown only when
	///     some realm carries a non-zero language id at +0x40. So one WESTERN realm serves all four and the player
	///     picks with the flag row, while Russian, Polish and Korean get realms of their own. A per-language bit for
	///     the other three would have nothing to select.
	///     <para />
	///     ⚠️ Which of bits 0 and 1 means what is NOT recovered - both map to English, and <c>FUN_00A68170</c> reads
	///     the same word and tests both. We send bit 0, which is what this has always sent, so the shipped behaviour
	///     of an English realm is unchanged by the decode.
	/// </remarks>
	public static class RealmLanguageFlags
	{
		public const uint Western = 0x1;
		public const uint Polish = 0x800;
		public const uint Russian = 0x400;
		public const uint Korean = 0x100000;

		public static uint For(RealmLanguage language)
		{
			return language switch
			{
				RealmLanguage.Russian => Russian,
				RealmLanguage.Polish  => Polish,
				RealmLanguage.Korean  => Korean,
				_                     => Western
			};
		}
	}
}
