using System;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     The GameAction type-id space, as the client bounds it.
	/// </summary>
	/// <remarks>
	///     Every reader asserts <c>i_nGameAction &gt;= e_Type_GC_GameAction &amp;&amp; i_nGameAction &lt;
	///     e_Type_GC_GameAction_End</c> (SpellData.cpp:1577), compiled as <c>(id - 0xCF09) &lt;= 0x11D</c>, so the valid ids
	///     are 0xCF09..0xD026 - 286 of them, 260 registered. Failing that assert is not a dropped packet: it pops an
	///     "Assertion Failed" dialog and takes the client down.
	///     <para>
	///         An id must therefore be validated against these bounds and never against another derivation of the same
	///         quantity. The descriptor table's stride is 0x104 (FUN_008c3910: <c>IMUL EAX,EAX,0x104; ADD EAX,0x25077c8</c>),
	///         and because that is four times 0x41 an index read at the smaller stride still looks aligned while landing four
	///         times too far up the table. The 68-entry docs/re/gameaction_types.json <c>typeId</c> column carries that
	///         error; trust its <c>enumId</c>, the value at descriptor+8, or docs/re/gameaction-type-ids.tsv.
	///     </para>
	/// </remarks>
	public static class GameActionTypeId
	{
		public const uint First = 0xCF09; // e_Type_GC_GameAction
		public const uint End = 0xD027;   // e_Type_GC_GameAction_End (exclusive)

		public static bool IsValid(uint typeId)
		{
			return typeId is >= First and < End;
		}

		/// <summary>
		///     Throws rather than letting an out-of-range id reach the client, where it is an assert dialog and a disconnect
		///     rather than a dropped packet.
		/// </summary>
		public static void Validate(uint typeId, string what)
		{
			if (!IsValid(typeId))
				throw new ArgumentOutOfRangeException(nameof(typeId), typeId,
					$"{what}: GameAction type 0x{typeId:X4} is outside the client's asserted range " +
					$"0x{First:X4}..0x{End - 1:X4} (SpellData.cpp:1577) - sending it crashes the client.");
		}
	}
}
