using System.Collections.Generic;
using Faolan.Core.Network.Codecs.World;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     A spell-cooldown override as a <see cref="GameActionData" /> element inside a GameActionListIIR - the wire
	///     mechanism by which the retail server sets a spell's recast timer on the client.
	/// </summary>
	/// <remarks>
	///     Not a codec: the encoding is GameActionDataCodec / GameActionListCodec in World/. What lives here is the action
	///     type combat cares about and its keyList value order, which is client-side static data rather than a message. The
	///     element grammar is positional and fully recovered, from the client deserializer FUN_00899a60 plus the live type
	///     catalog in docs/re/gameaction_types.json (see docs/re/gameactionlist-grammar.md):
	///     <code>
	///     [u32 typeId][u32 field][u32 version=4][u32 subCount]
	///     [u32 v(0x2d5)][u32 v(0x2d6)][u32 v(0x2f2)][u32 v(0x2f5)]   four always-present positional int values
	///     descriptor keyList values, in this type's order; u32 unless the key is 0x4d9/0x4da, a std::string
	///     </code>
	///     The keys are not on the wire - they live in the client's static per-type descriptor - so the wire carries only
	///     the values, in the type's order. For SetSpellRecastTime that order is [0x463 spell id][0x2f9 recast], the recast
	///     in centiseconds (the client applies recast x 0.01 s; FUN_007e1080, "GameCode/Magic/Recast").
	///     <para>
	///         The recast handler (FUN_008b0f50, "GameCode/GameAction/SetSpellRecastTime") reads only the keyList values and
	///         never the four fixed ints, so those carry the GameActionData constructor's default seeds (FUN_008985c0:
	///         0x2d5 = 1, 0x2d6 = 0, 0x2f2 = 1, 0x2f5 = 0). subCount is 0 and field is 0 for a bare override.
	///     </para>
	///     <para>
	///         These type ids come from docs/re/gameaction-type-ids.tsv, the contiguous enum, and not from
	///         docs/re/gameaction_types.json, whose typeId column is a step-4 sequence that is not the wire value. The corpus
	///         settles it: the capture-verified travel action is 0xD00D, which the tsv names
	///         StartServerSideScryScriptWithParams - matching its ScriptParams string - while the json names it TeamFormula.
	///         Under the json a recast override would be 0xCF81, which the tsv names Feared.
	///     </para>
	/// </remarks>
	public static class SpellRecastActions
	{
		/// <summary>
		///     GameAction wire type ids and keyList order (docs/re/gameaction-keylists.tsv). The two setters state a recast
		///     absolutely; the two Change twins are the feat and alternate-advancement modifiers that add a delta to a base
		///     the client already holds (FUN_007e1230), which is why arming a cooldown uses a setter.
		/// </summary>
		public const uint SetSpellRecastTimeType = 0xCFEA;     // keyList: [0x463 spell, 0x2f9 recast]
		public const uint SetSpellLineRecastTimeType = 0xCF55; // keyList: [0x2d3 line,  0x2f9 recast]
		public const uint ChangeRecastTimeType = 0xCF27;       // keyList: [0x4c3 spell, 0x2f9 delta]
		public const uint ChangeLineRecastTimeType = 0xCF4D;   // keyList: [0x2d3 line,  0x2f9 delta]

		/// <summary>
		///     A single SetSpellRecastTime element: starts the spell's recast timer at
		///     <paramref name="recastCentiseconds" /> on the receiving client, which applies it as value * 0.01 s
		///     (FUN_007e1080, "GameCode/Magic/Recast").
		/// </summary>
		public static GameActionData SetSpellRecastTime(uint spellId, uint recastCentiseconds)
		{
			return Element(SetSpellRecastTimeType, 0, 0, [spellId, recastCentiseconds]);
		}

		/// <summary>
		///     The line-scoped twin: arms every rank sharing <paramref name="lineId" /> at once, which is what the client's
		///     own SetSpellLineRecastTime means and why all five ranks of a spell share one timer.
		/// </summary>
		public static GameActionData SetSpellLineRecastTime(uint lineId, uint recastCentiseconds)
		{
			return Element(SetSpellLineRecastTimeType, 0, 0, [lineId, recastCentiseconds]);
		}

		/// <summary>
		///     A single ChangeRecastTime element: adds <paramref name="deltaCentiseconds" /> to the spell's existing base. A
		///     modifier rather than a setter, for the feat and alternate-advancement case it models.
		/// </summary>
		public static GameActionData ChangeRecastTime(uint spellId, uint deltaCentiseconds)
		{
			return Element(ChangeRecastTimeType, 0, 0, [spellId, deltaCentiseconds]);
		}

		/// <summary>
		///     Any single all-u32 GameActionData element, from its type id and descriptor-ordered keyList values. The string
		///     keys 0x4d9/0x4da are not modelled here, since the recast and stat-modifier setters are all-u32.
		/// </summary>
		public static GameActionData Element(uint typeId, uint field, uint subCount, IReadOnlyList<uint> keyValues)
		{
			return new GameActionData(typeId, field, subCount, keyValues);
		}
	}
}
