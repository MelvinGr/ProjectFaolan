using System.Collections.Generic;
using Faolan.Core.Network.Codecs.World;

namespace Faolan.Core.Network.Codecs.Combat
{
	// A spell-cooldown OVERRIDE as a GameActionData element inside a GameActionListIIR (0xF508F4C1) - the wire
	// mechanism by which the retail server sets a spell's recast timer on the client (#271).
	//
	// Not a codec: the encoding is `GameActionDataCodec` / `GameActionListCodec` in World/. What lives here is the ONE
	// action type combat cares about and its keyList value order, which is client-side static data rather than a
	// message. The element grammar is POSITIONAL and fully recovered: from the client deserializer FUN_00899a60 plus
	// the live Frida type catalog docs/re/gameaction_types.json (docs/re/gameactionlist-grammar.md). An element is:
	//
	//   [u32 typeId][u32 field][u32 version=4][u32 subCount]
	//   [u32 v(0x2d5)][u32 v(0x2d6)][u32 v(0x2f2)][u32 v(0x2f5)]     // four ALWAYS-present positional int values
	//   <descriptor keyList values, in this type's order; u32 unless the key is 0x4d9/0x4da = std::string>
	//
	// The keys are NOT on the wire (they live in the client's static per-type descriptor); the wire carries only the
	// VALUES, in the type's order. For SetSpellRecastTime (0xcfea) that order is [0x463 = spell id][0x2f9 = recast],
	// recast in CENTISECONDS (client applies recast x 0.01 s, DAT_01f2800c; FUN_007e1080 "GameCode/Magic/Recast").
	//
	// The recast handler (FUN_008b0f50, "GameCode/GameAction/SetSpellRecastTime") reads ONLY the keyList values
	// (spell + 0x2f9); it never reads the four fixed ints, so those carry the GameActionData ctor's default seeds
	// (FUN_008985c0: 0x2d5=1, 0x2d6=0, 0x2f2=1; 0x2f5=0 - a real create-editor element carried 0x2f5=9, but the recast
	// handler ignores it, and any value parses). subCount=0 (no sub-records) and field=0 for a bare override.
	//
	// ⚠️ These type ids come from `docs/re/gameaction-type-ids.tsv` (the CONTIGUOUS enum), NOT from
	// `docs/re/gameaction_types.json`, whose `typeId` column is a step-4 sequence that is not the wire value. The
	// corpus settles it: the capture-verified travel action is 0xD00D and the tsv calls that
	// `StartServerSideScryScriptWithParams` (which is what its ScriptParams string IS), while the json calls it
	// `TeamFormula`. Under the json this file's ChangeRecastTime read 0xCF81, which the tsv names `Feared` - so the
	// pre-#767 builder would have sent the client a FEAR. It was never wired, which is the only reason that never
	// shipped. Naming a recast type off the json is the mistake to not repeat.
	public static class SpellRecastActions
	{
		// GameAction wire type ids + keyList ORDER (docs/re/gameaction-keylists.tsv). The two SETTERS state a recast
		// absolutely; the two Change* twins are the feat/AA MODIFIERS that add a delta to a base the client already
		// holds (FUN_007e1230), which is why arming a cooldown uses a setter.
		public const uint SetSpellRecastTimeType = 0xCFEA;     // keyList: [0x463 spell, 0x2f9 recast]
		public const uint SetSpellLineRecastTimeType = 0xCF55; // keyList: [0x2d3 line,  0x2f9 recast]
		public const uint ChangeRecastTimeType = 0xCF27;       // keyList: [0x4c3 spell, 0x2f9 delta]
		public const uint ChangeLineRecastTimeType = 0xCF4D;   // keyList: [0x2d3 line,  0x2f9 delta]

		// A single SetSpellRecastTime element: start `spellId`'s recast timer at `recastCentiseconds` on the receiving
		// client. The client applies it as `value * 0.01` s (FUN_007e1080, "GameCode/Magic/Recast").
		public static GameActionData SetSpellRecastTime(uint spellId, uint recastCentiseconds)
		{
			return Element(SetSpellRecastTimeType, 0, 0, [spellId, recastCentiseconds]);
		}

		// The LINE-scoped twin: arms every rank sharing `lineId` at once, which is what the client's own
		// SetSpellLineRecastTime means and why all five Chill ranks share one 4 s timer.
		public static GameActionData SetSpellLineRecastTime(uint lineId, uint recastCentiseconds)
		{
			return Element(SetSpellLineRecastTimeType, 0, 0, [lineId, recastCentiseconds]);
		}

		// A single ChangeRecastTime element: ADD `deltaCentiseconds` to `spellId`'s existing base. A modifier, not a
		// setter - kept for the feat/AA case it actually models.
		public static GameActionData ChangeRecastTime(uint spellId, uint deltaCentiseconds)
		{
			return Element(ChangeRecastTimeType, 0, 0, [spellId, deltaCentiseconds]);
		}

		// Any single all-u32 GameActionData element, from its typeId + descriptor-ordered keyList VALUES. (String keys
		// 0x4d9/0x4da are not modelled here - the recast/stat-modifier setters are all-u32.)
		public static GameActionData Element(uint typeId, uint field, uint subCount, IReadOnlyList<uint> keyValues)
		{
			return new GameActionData(typeId, field, subCount, keyValues);
		}
	}
}
