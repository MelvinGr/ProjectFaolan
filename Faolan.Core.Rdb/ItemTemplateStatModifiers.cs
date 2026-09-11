using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     Parses the item template's stat-modifier region - the full tooltip stat list (attributes, vitals and
	///     ratings), distinct from the strictly-decreasing property table (which only carries the 972 primary
	///     attribute, value 74, quality 499, bag 104). See <c>docs/rdb/item-template-stats.md</c>.
	/// </summary>
	/// <remarks>
	///     Each modifier is a fixed 10-word (u32 LE) record, anchored on a 6-word signature:
	///     <code>
	///     [kind][0][4][0][1][0][2][9][statId][amount]
	///               \------ 4,0,1,0,2,9 ------/
	///     </code>
	///     <c>statId</c> is a wire stat id (the same space as the 0x96B8DC59 vitals / snapshot stat block);
	///     <c>amount</c> is the bonus at wire scale (divide by 100 for vitals and Magic Damage, by 10 for attributes,
	///     by 1 for ratings - <c>CharacterStatId.GetScale()</c>).
	///     <para />
	///     <c>kind</c> is the word that opens the record, and it is what makes the region heterogeneous: an
	///     effect-kind tag (a ~53000-range id), not padding. Spell templates - parsed by this same code - mix several,
	///     and the kind is what says how to read <c>amount</c>:
	///     <list type="bullet">
	///         <item>53045 / 53189 / 53220 / 53221 - flat modifier (Battle Cry +62 Hit Rating, Fend +1000 Armor)</item>
	///         <item>53067 - percent modifier, x100 (Philtre of Guile 200 = +2% Critical Rating)</item>
	///         <item>53219 / 53002 / 53007 / 53013 - health effects: heals and the -100000000 drain/kill sentinel</item>
	///     </list>
	///     A consumer must branch on the kind: reading a 53067 record as flat turns "+2%" into "+200". The
	///     classification above is empirical (<c>docs/rdb/spell-stat-effects.md</c>); the server honours only what it
	///     can justify - see <c>BuffStatEffects</c>.
	///     <para />
	///     Item templates are heterogeneous too. <c>--report item-effect-kinds</c> censuses the 4,753 decryptable
	///     keyed templates: 24 distinct kinds, and the region's second-commonest member is
	///     <c>AddLayeredSkinnedMesh</c> (5,632 elements, a mesh id in a slot a positional reader would file as a stat
	///     id). What is true is narrower: exactly one kind - 53045 <c>Modify</c> - carries a stat this server applies,
	///     so a whitelisted consumer sees a homogeneous region. That is a property of the whitelist, not of the
	///     catalog; widen <c>ApplicableStatIds</c> and it stops holding.
	///     <para />
	///     Anchor on the signature, not a raw [2][9] scan: the raw scan yields ~2500 hits (a third of them
	///     coincidental 2,9 word pairs); the signature yields the ~1550 real records (the word before the <c>4</c> is
	///     0 in 100% of them). Even so a few signature hits carry an RDB resource-type id as statId (1000017/1000018)
	///     or garbage (0x3de071, 0xd00d) - so statId is bounded to the real stat-id range (0, 0x1000). Validated 3/3
	///     against the in-client Royal Signet tooltip (+30 Con / +52 Magic Damage / +15 Hit Rating; template
	///     3774542).
	/// </remarks>
	public static class ItemTemplateStatModifiers
	{
		// Stat ids are small (the largest real one is ~0x35d); this bound drops the resource-ref and garbage
		// signature hits without needing a full whitelist here (the importer whitelists to the stats it applies).
		private const uint MaxStatId = 0x1000;

		// `AddPercentSkillModifier` and its two stat slots. The kind carries no key 0, so it is invisible to the
		// stat-slot rule `ParseSpellStatEffects` uses for every other kind; these are the keys the client's own
		// tooltip branch reads it by. Per-type, like every unnamed key - 66/67 are the damage and cooldown deltas on
		// `UpgradeCombo` and the DPS/crit slots on `ChangeInheritedWeaponAttack`.
		public const uint PercentOfStatKind = 53189;
		private const int PercentOfStatSourceKey = 66;
		private const int PercentOfStatTargetKey = 67;

		/// <summary>
		///     All stat-modifier records in a decrypted template body (the span after the 12-byte RDB-Id header), in
		///     file order. Signature-anchored; statId bounded to the real range. Duplicates are preserved (a template
		///     never repeats a stat id in the corpus, but the parser stays faithful to what is on the wire).
		/// </summary>
		public static IReadOnlyList<Modifier> Parse(ReadOnlySpan<byte> body)
		{
			return Scan(body, true);
		}

		/// <summary>
		///     The records <see cref="Parse" /> rejects - those whose payload's first word is outside the stat-id
		///     range. That bound is right for a stat modifier and wrong for the rest: a feat's spell modifier puts a
		///     spell-line id there and the magnitude second, so <c>Decomposition</c> rank 5 reads (41000, 3500) =
		///     "+35.00% on Necromancer line 0, Flesh to Worms". Same scan, opposite filter - deliberately not a second
		///     copy of the signature walk, so the two can never disagree about where a record is, only about which
		///     ones count.
		///     <para>
		///         The kind is what says how to read one, and the kinds are not anonymous: they are GameAction type
		///         ids, catalogued in <see cref="GameActionTypes" /> with their key lists. 53075 is
		///         <c>ChangeLineHitPercent</c> (a feat's spell modifier - see
		///         <c>RdbFeatImporter.ParseSpellModifiers</c> and <c>--report feat-spell-modifiers</c>).
		///     </para>
		///     <para>
		///         53051 is <c>CastNano</c> (key 39) - "nano" being Funcom's Anarchy Online word for a spell. On an
		///         item template its payload is a <c>Spells.Id</c>: the spell the item casts when used, which is the
		///         source <c>Item.OnUseSpell</c> wants. Measured over 26 dumped templates (12 with an on-use spell
		///         harvested from captures, 14 without): 8 of the 12 controls recover exactly, each resolving to the
		///         right spell name - Raw Beets 112936 "Hearty Meal", Daggamalt 180250 "Health Potion Effect 10",
		///         Double XP 4552864 "Double XP" - with zero false positives across the 14. Of the 4 misses both loot
		///         caches carry no spell in the template at all, Totem of Origins carries a different spell than the
		///         capture recorded, and Moonspill carries its under another prefix. Not imported yet; open work
		///         tracked in Gitea #762.
		///     </para>
		/// </summary>
		public static IReadOnlyList<Modifier> ParseRejected(ReadOnlySpan<byte> body)
		{
			return Scan(body, false);
		}

		/// <summary>
		///     The accounting check: every position in <paramref name="body" /> that looks like the head of a
		///     GameAction element but is not the offset of one <see cref="ParseElements" /> returned. Empty means the
		///     scan accounted for every element in the record.
		///     <para>
		///         An anchor that matches only leaf elements reads 1 element out of 3 from a 754-byte record -
		///         dropping what the spell applies, how far, to how many and how often - while every test that
		///         asserts on the elements that were parsed stays green. A gate that shares the parser's blind spot is
		///         not a gate, which is why this question is asked explicitly.
		///     </para>
		///     <para>
		///         This is the RDB analogue of the codec rule that <c>Parse</c> ends in <c>EnsureAtEnd()</c>: account
		///         for the data or say so. It cannot be an xUnit gate - <c>Faolan.Tests</c> runs from a plain clone
		///         with no RDB - so it is surfaced by the importer, which is where the bytes are, and its count rides
		///         the import summary rather than being silently dropped.
		///     </para>
		///     <para>
		///         The head signature is deliberately the same one the scan itself anchors on minus the parts that
		///         vary: a type id inside the client's asserted descriptor range with the version word <c>4</c> eight
		///         bytes later. That pair is specific enough to be worth reporting - a coincidental hit needs a u32 in
		///         a 286-wide window followed by an exact <c>4</c> - and any real false positive is itself worth
		///         seeing rather than assuming away.
		///     </para>
		/// </summary>
		public static IReadOnlyList<int> UnparsedElementHeads(ReadOnlySpan<byte> body)
		{
			var parsed = new HashSet<int>();
			foreach (var e in ParseElements(body))
				parsed.Add(e.Offset);

			var orphans = new List<int>();
			for (var off = 0; off + 12 <= body.Length; off++)
			{
				var kind = W(body, off);
				if (kind < GameActionTypes.FirstId || kind > GameActionTypes.LastId || W(body, off + 8) != 4)
					continue;

				if (!parsed.Contains(off))
					orphans.Add(off);
			}

			return orphans;
		}

		/// <summary>
		///     Every GameAction element in the body, whole - kind plus one value per key of that kind's key list.
		///     This is the primitive both readers above are expressed on; use it when a record's later slots matter
		///     (a <c>SpellStatModifier</c> carries spell + stat + value + amount, and the pair readers see only the
		///     first two).
		/// </summary>
		public static IReadOnlyList<Element> ParseElements(ReadOnlySpan<byte> body)
		{
			var elements = new List<Element>();
			// The element is [typeId][field][4][subCount][v(0x2d5)][v(0x2d6)][v(0x2f2)][v(0x2f5)][one u32 per key].
			// In a TEMPLATE those middle six words are always `4,0,1,0,2,9`, which is what makes them usable as an
			// anchor: match them at off..off+20 with the preceding [field][0] word, and the typeId sits two words
			// BEFORE the anchor - so it is unreadable for an element that starts the body (off < 8), and those report
			// kind 0, which no policy honors.
			//
			// Scan byte by byte, not word by word. The region is not 4-byte aligned to the body in most templates -
			// Blackwolf Cap's records start at body offset 297 - and a word-aligned scan cannot see those, silently
			// returning zero records (2873 of 3248 templates report "no stats" while the client happily applies a
			// full list from the very same bytes). Byte-granular scanning recovers them exactly and changes nothing
			// for the templates that were already aligned.
			//
			// An element may carry sub-records, and an anchor that demands `subCount == 0` with the two fixed ints
			// `[1][0]` only matches the leaf shape. `Gangrenous Stench` (221070) carries three elements and that form
			// matches exactly one:
			//
			//   @530 FilteredAreaFormula  subCount=2  fixed [1][0][2][9]     -> spell 221069, radius 7, targets 12
			//   @606 FilteredAreaFormula  subCount=2  fixed [-1][400][2][9]  -> the same, applied every 400 cs
			//   @682 SetSpellRecastTime   subCount=0  fixed [1][0][2][9]     -> the only leaf
			//
			// A leaf-only anchor hides the spell's whole AoE - what it applies, how far, to how many, and how often -
			// and with it every aura and every effect chain whose link is not a leaf element. The two 7/12 values are
			// the "7.00m radius up to 12 targets" of the in-client tooltip, and 400 cs is its "every 4 seconds".
			//
			// The layout, recovered from those three and true of all of them:
			//
			//   [typeId][field][version=4][subCount][subCount x 3 words][0x2d5][0x2d6][0x2f2=2][0x2f5=9][values...]
			//
			// What is stable is the trailing `[2][9]` pair, not the leading `[1][0]` - @606 carries `[-1][400]` there,
			// which is the aura's period. So the anchor keeps its precision by matching the typeId's range, the
			// version word, and that trailing pair, and derives the sub-record run from `subCount`.
			//
			// `maxSubRecords` is a sanity bound, not a grammar claim: an unbounded count turns a garbage word into a
			// wild key offset. 64 because the widest shipped element carries 40 (a `CastNano` listing ten spell ranks
			// as two sub-records each - spell 1126058, subCount 20, tiling exactly 240 bytes into fixed ints
			// [1][0][2][9] and its key). At 16 it silently rejects 22 real elements; the accounting check below is
			// what makes that visible, and it is 0 at 64.
			const uint maxSubRecords = 64;
			const int subRecordWords = 3;

			for (var off = 0; off + 32 <= body.Length; off++)
			{
				var kind = W(body, off);
				if (kind < GameActionTypes.FirstId || kind > GameActionTypes.LastId || W(body, off + 4) != 0 || W(body, off + 8) != 4)
					continue;

				var subCount = W(body, off + 12);
				if (subCount > maxSubRecords)
					continue;

				// The four fixed ints sit after the sub-record run, and none of them is constant: they are the
				// element's 0x2d5/0x2d6/0x2f2/0x2f5 values, and across the spell catalog they take `[1][0][3][9]`,
				// `[-1][300][3][9]`, `[0][0][100][0]`, `[0][0][18][0]` and more. `0x2d6` is where an aura's period
				// lives (400 = every 4 s on Gangrenous Stench), so matching any of them as a signature would discard
				// a mechanic. What identifies an element is its type plus the version word, which is specific enough
				// on its own: a u32 inside the client's 286-wide asserted descriptor range with an exact `4` eight
				// bytes later.
				var fixedAt = off + 16 + (int)subCount * subRecordWords * 4;
				if (fixedAt + 16 > body.Length)
					continue;

				// The anchor is the `version` word, so the value offset and the reported element offset/width are
				// what every caller expects for a leaf.
				var valuesAt = fixedAt + 16;
				// How many values follow is a property of the kind, not a constant: an element carries exactly one
				// u32 per key-list entry (docs/re/gameactionlist-grammar.md). An unregistered kind gets two, which is
				// all any caller of the pair readers below uses.
				var type = GameActionTypes.Get(kind);

				// The payload is walked slot by slot rather than indexed, because a text slot is `[u16 byteLength]
				// [bytes]` and so shifts every slot after it - by an odd number of bytes - which is why a
				// text-carrying element has no fixed width and why a positional read past one takes the string's
				// length for the next slot's value. `SetMorphValue`'s "mesh name after the element" is this slot.
				//
				// The u16 is the client's own string primitive, and the only one on this path: the deserializer
				// `FUN_00899a60` takes the string branch for exactly `GameActionStat_e` 0x4d9/0x4da and calls
				// `FUN_0048f840` -> `FUN_00cbf840` mode 0, which reads a u16 and treats >= 0x8000 as an escape into a
				// long form; the writer `FUN_00899440` emits the same u16 with the same escape, and neither branches
				// on the type. It is also what the shipped records measure: of the 880 text-carrying elements in the
				// feat and spell catalogs, 653 tile exactly under this reading (`--report feat-effect-kinds`), and the
				// strings come out whole - "spell_curseofflies" as a 0x12-byte slot, "arm" as a 3-byte one, neither
				// carrying a NUL.
				//
				// 7 elements do not - all of them `HeadText`, reading `[u32 7]["Dimach\0"][u32 3]` two bytes wider
				// than the u16 form allows. Deliberately not modelled: they are retired data, sitting in the same
				// low-id feat templates (3753, 12362..12386) that carry the retired kind 53044 and a
				// `ChangeHitPercent` padded with terminator words, while every u16 record in either catalog is
				// template id 791547 or higher. A second length width here would be a tolerant reader for bytes the
				// client itself refuses to stream.
				//
				// An unregistered kind has no key list, so it keeps the two u32 slots the pair readers below need and
				// nothing more. That is not a decode gap waiting to be filled: the catalog covers every id the client
				// registers, and the ids it does not are retired (GameActionTypes.UnregisteredIds), so an element
				// carrying one is data the client refuses to stream at all.
				var keys = type?.Keys ?? [GameActionTypes.KeyStat, GameActionTypes.KeyValue];
				var values = new List<uint>(keys.Length);
				var texts = new List<string?>(keys.Length);
				var at = valuesAt;
				foreach (var key in keys)
				{
					// Read as many slots as the body holds, not as many as the key list wants. Where an element is
					// followed by too few bytes for its full payload, dropping it would hide the one thing worth
					// seeing: an element narrower than its type says. It is still reported - with a short value
					// array, so `Value(key)` returns null for the slots that are not there and every caller that
					// needs a whole modifier rejects it on its own terms rather than on this scan's silence.
					if (at + 4 > body.Length)
						break;

					if (!GameActionTypes.TextKeys.Contains(key))
					{
						values.Add(W(body, at));
						texts.Add(null);
						at += 4;
						continue;
					}

					// A length that runs past the body is a mis-anchored element, not a long string: stop rather
					// than resynchronise on bytes that are not this element's. `>= 0x8000` is the client's own
					// escape into a long form, which no shipped template uses - so it is a mis-anchor here too.
					var length = body.ReadU16Le(at);
					if (length >= 0x8000 || at + 2 + length > body.Length)
						break;

					// Latin1, the wire's string encoding. The count does NOT include a terminator in any shipped
					// record; the trim is there so one would not become part of the value if it ever did.
					texts.Add(Encoding.Latin1.GetString(body.Slice(at + 2, length)).TrimEnd('\0'));
					values.Add(length);
					at += 2 + length;
				}

				// The sub-record run is carried, not dropped. It is what distinguishes two elements that state the
				// same effect: of the 1,285 elements that repeat another element of the same record with identical
				// decoded slots, 1,003 differ here and only 282 are byte-identical. A reader that stops at the
				// decoded slots reports all 1,285 as duplicated data.
				var subs = new uint[subCount * subRecordWords];
				for (var s = 0; s < subs.Length; s++)
					subs[s] = W(body, off + 16 + s * 4);

				elements.Add(new Element(off, kind, [.. values], [.. texts], at - off, W(body, fixedAt), W(body, fixedAt + 4), subs));
			}

			return elements;
		}

		/// <summary>
		///     The element region is not one list - it is a sequence of count-framed lists, and this is the split.
		///     Each group is introduced by a 12-byte header <c>[2][listId][(count + 1) * 0x3F1]</c> sitting
		///     immediately before its first element.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The frame is the client's ordinary list count word - the same <c>(n + 1) * 0x3F1</c> the wire uses
		///         (see <c>ListFrame</c>), which is why one-element and two-element frames read as constants in the 12
		///         bytes between elements.
		///     </para>
		///     <para>
		///         This is what an "element stated twice, verbatim" is: the twins are in different lists.
		///         <c>Hellfire Stream</c> (8600) holds four - list <c>0</c> with its five level bands plus the
		///         <c>CastNano</c> and the recast, lists <c>29</c> and <c>30</c> with one <c>Modify</c> each, and list
		///         <c>31</c> with two more bands that repeat list 0's first two value for value. Reading the region as
		///         one stream makes those look like duplicated data; they are two lists that happen to share an entry.
		///     </para>
		///     <para>
		///         The list id is carried, not interpreted. List <c>0</c> is the bulk (57,579 of 59,705 elements) and
		///         the other ids skew hard by kind - <c>20</c> is <c>StopParticleEffect</c> / <c>Fatality</c> /
		///         <c>CastNano</c>, <c>33</c> is <c>SetSpellRecastTime</c> and the area and cone formulas - which
		///         reads like a trigger (on-expiry, on-hit) but nothing has pinned that, so no consumer branches on it.
		///     </para>
		/// </remarks>
		public static IReadOnlyList<ElementList> ParseElementLists(ReadOnlySpan<byte> body)
		{
			var elements = ParseElements(body);
			var lists = new List<ElementList>();

			var i = 0;
			while (i < elements.Count)
			{
				var head = elements[i].Offset - ListHeaderBytes;
				if (head < 0 || W(body, head) != ListHeaderTag)
					break;

				var frame = W(body, head + 8);
				if (frame == 0 || frame % ListFrameUnit != 0)
					break;

				// The frame states (count + 1); a count that overruns what was parsed means this header is not one,
				// so the walk stops rather than inventing a group. Callers get the lists it could account for and
				// compare `Sum(Count)` against `ParseElements(body).Count` to see whether it accounted for all.
				var count = (int)(frame / ListFrameUnit) - 1;
				if (count <= 0 || i + count > elements.Count)
					break;

				var members = new List<Element>(count);
				for (var j = i; j < i + count; j++)
					members.Add(elements[j]);

				lists.Add(new ElementList(head, W(body, head + 4), members));
				i += count;
			}

			return lists;
		}

		/// <summary>One count-framed group of elements: where its header sits, its id, and its members.</summary>
		public readonly record struct ElementList(int Offset, uint Id, IReadOnlyList<Element> Elements);

		/// <summary>
		///     One condition on an element, named by the client's own assert - <c>invalid criterion
		///     id/value/operator</c> (<c>FUN_006e9f20</c>, which bounds the operator to <c>0..0x401</c>).
		/// </summary>
		public readonly record struct Criterion(uint Id, uint Value, uint Operator);

		// The element-list header: `[2][listId][(count + 1) * 0x3F1]`, 12 bytes immediately before the group's first
		// element. `2` is constant across every shipped list header in the spell catalog.
		private const int ListHeaderBytes = 12;
		private const uint ListHeaderTag = 2;
		private const uint ListFrameUnit = 0x3F1;

		/// <summary>
		///     The records that really are a modifier of the carrier's own stats, read by key rather than by position.
		///     Three conditions, each of them a property of the type's key list rather than of the bytes:
		///     <list type="number">
		///         <item>
		///             it has a <c>Stat</c> slot (key 0) - <c>NanoResistance</c> (152, Value),
		///             <c>DisplayLearnAbility</c> (Line, 187) and <c>AddPercentSkillModifier</c> (66, SkillType, 67,
		///             Value) have none, so a pair view's first word is a strain, an ability ref or an unnamed slot
		///             filed as a stat id;
		///         </item>
		///         <item>
		///             it has a magnitude - <c>Value</c> (39), or the interpolated family's low endpoint (174) - so a
		///             pure damage record (<c>Hit</c> is Stat plus three unnamed slots) is not mistaken for a bonus;
		///         </item>
		///         <item>
		///             it carries no <c>Spell</c> (88) or <c>Line</c> (1) slot, because such a record modifies that
		///             spell's properties rather than the carrier's stats. That is the whole of
		///             <c>SpellStatModifier</c> / <c>SpellLineStatModifier</c> / <c>TargetSpellLineStatModifier</c>,
		///             which have their own table.
		///         </item>
		///     </list>
		///     <para>
		///         For the interpolated kinds <c>WireAmount</c> is the value at the ramp's low level, not a
		///         level-independent bonus: the record is <c>lerp(low@lowLevel, high@highLevel)</c> over the carrier's
		///         level (<c>docs/re/gameactionlist-grammar.md</c>). Applying it alone under-grants everyone above
		///         that level, so <see cref="Modifier.Ramp" /> carries the whole ramp beside it - resolve it through
		///         <see cref="LevelRamp.ValueAt" /> rather than reading the magnitude flat.
		///     </para>
		/// </summary>
		public static IReadOnlyList<Modifier> ParseStatModifiers(ReadOnlySpan<byte> body)
		{
			var mods = new List<Modifier>();
			foreach (var e in ParseElements(body))
			{
				if (e.Type is not { } type || type.SlotOf(GameActionTypes.KeySpell) >= 0 || type.SlotOf(GameActionTypes.KeyLine) >= 0)
					continue;

				var stat = e.Value(GameActionTypes.KeyStat);
				var amount = e.Value(GameActionTypes.KeyValue) ?? e.Value(GameActionTypes.KeyLowValue);
				if (stat is null or 0 or >= MaxStatId || amount is null)
					continue;

				mods.Add(new Modifier(e.Kind, stat.Value, amount.Value, e.Ramp, e.Fixed2d6, e.MaxAmount, e.MaxRamp, [.. e.Criteria]));
			}

			return mods;
		}

		/// <summary>
		///     The stat effects a template's region carries, read by key rather than by position.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A pair view returns payload words 0 and 1 whatever keys they belong to, and that is only
		///         (stat, amount) for the 12 kinds whose key list opens with key 0. For the other 41 it silently files
		///         another field's value as a stat id: <c>NanoResistance</c> <c>[152, 39]</c> reports its key-152 word
		///         as <c>Team</c>/<c>State</c>/<c>SellModifier</c>, <c>SetSpellLineRecastTime</c> <c>[1, 39]</c>
		///         reports a spell line as a stat, and <c>StartParticleEffect</c> <c>[209, ...]</c> reports the byte
		///         length of a string. That is 1,200 of the 6,133 shipped rows.
		///     </para>
		///     <para>
		///         The magnitude is the first key after the stat in the type's own key list, which is one rule that
		///         gets all twelve right: key 39 <c>Value</c> on <c>Modify</c>, key 2 on <c>Hit</c>, key 168 on
		///         <c>InterpolatedHit</c>, key 174 (the ramp's low endpoint) on <c>InterpolatedModify</c>, key 70 on
		///         <c>AddAbsorb</c>, and key 39 again on <c>Set</c>, whose stat is at slot 1 rather than slot 0. It is
		///         also exactly what a positional read does by accident wherever the stat happens to come first, so
		///         this changes no value that was already right.
		///     </para>
		/// </remarks>
		public static IReadOnlyList<Modifier> ParseSpellStatEffects(ReadOnlySpan<byte> body)
		{
			var mods = new List<Modifier>();
			foreach (var e in ParseElements(body))
			{
				// An unregistered kind has no key list, so nothing says where its stat is - and the catalog covers
				// every id the client registers, which makes an unregistered one retired data rather than a gap.
				if (e.Type is not { } type)
					continue;

				// `AddPercentSkillModifier` is the one kind whose stat is not findable by the rule below, and it is
				// not an omission in its key list - it carries two stats and neither is key 0. The client's own
				// tooltip branch (0x014a1b52) reads it as `key 67 += key 39 % of key 66`, picking between a same-stat
				// and a different-stat sentence by comparing the two, which is what names them: 66 is the source and
				// 67 the stat being raised.
				if (e.Kind == PercentOfStatKind)
				{
					var target = e.Value(PercentOfStatTargetKey);
					var source = e.Value(PercentOfStatSourceKey);
					var pct = e.Value(GameActionTypes.KeyValue);
					if (target is null or 0 or >= MaxStatId || source is null or 0 or >= MaxStatId || pct is null)
						continue;

					mods.Add(new Modifier(e.Kind, target.Value, pct.Value, e.Ramp, e.Fixed2d6, e.MaxAmount, e.MaxRamp,
						[.. e.Criteria], source.Value));
					continue;
				}

				var statSlot = type.SlotOf(GameActionTypes.KeyStat);
				if (statSlot < 0 || statSlot + 1 >= type.Keys.Length)
					continue; // this kind carries no stat, or no slot after it to hold a magnitude

				var stat = e.Value(GameActionTypes.KeyStat);
				var amount = e.Value(type.Keys[statSlot + 1]);
				if (stat is null or 0 or >= MaxStatId || amount is null)
					continue;

				mods.Add(new Modifier(e.Kind, stat.Value, amount.Value, e.Ramp, e.Fixed2d6, e.MaxAmount, e.MaxRamp, [.. e.Criteria]));
			}

			return mods;
		}

		// One element walk for both pair readers. `inStatRange` picks which side of the stat-id bound to keep.
		private static IReadOnlyList<Modifier> Scan(ReadOnlySpan<byte> body, bool inStatRange)
		{
			var mods = new List<Modifier>();
			foreach (var e in ParseElements(body))
			{
				var statId = e.First;
				if (statId == 0 || statId < MaxStatId != inStatRange)
					continue;

				mods.Add(new Modifier(e.Kind, statId, e.Second, e.Ramp, e.Fixed2d6, e.MaxAmount, e.MaxRamp, [.. e.Criteria]));
			}

			return mods;
		}

		// Reads a u32 at a byte offset - deliberately not word-indexed, see the scan note in ParseElements.
		private static uint W(ReadOnlySpan<byte> body, int byteOffset)
		{
			return body.ReadU32Le(byteOffset);
		}

		/// <summary>
		///     One record: its effect kind (the opening word), the wire stat id, and the amount as it sits in the
		///     template, not yet scaled. Divide by <c>CharacterStatId.GetScale()</c> for display - after checking that
		///     the kind means "flat".
		/// </summary>
		/// <remarks>
		///     This is the pair view: the first two payload words, which is (stat, value) for the plain modifier kinds
		///     and something else for the rest - a <c>SpellStatModifier</c> puts the spell first and the stat second.
		///     Reach for <see cref="Element" /> when the kind is not known to be a plain modifier.
		///     <para />
		///     <c>Ramp</c> is non-null exactly for the interpolated kinds, whose <c>WireAmount</c> is only the ramp's
		///     low endpoint - a consumer that applies it flat under-grants everyone above that level. It carries the
		///     whole ramp, low endpoint included, so a caller never has to pair it back up with <c>WireAmount</c> (and
		///     cannot get the signedness wrong doing so: the low value is signed and <c>WireAmount</c> is not).
		///     <para />
		///     <c>PeriodCentiseconds</c> is the element's 0x2d6: 0 means the record applies once, non-zero that it
		///     repeats on that cadence. Carried because the two are different mechanics on the same stat -
		///     <c>Daggamalt</c> heals 405 HP instantly and 67.5 HP every 100 cs - and a reader that drops it cannot
		///     tell them apart.
		///     <para />
		///     <c>MaxAmount</c>/<c>MaxRamp</c> are the range's upper endpoint and its ramp, non-null exactly for the
		///     types that state a range (<see cref="Element.MaxAmount" />). <c>WireAmount</c> is then only the lower
		///     endpoint, and a consumer that reads it alone reports a 58.40-71.40 hit as a flat 58.40.
		///     <para />
		///     <c>Criteria</c> is the element's own condition run, carried through rather than dropped. An element
		///     that states one applies only when it evaluates - without it a conditional bonus is granted always,
		///     which is what 987 of the catalog's 1,117 apparent duplicate elements differ by.
		///     <para />
		///     <c>SourceStatId</c> is non-null only on <c>AddPercentSkillModifier</c> (53189), the one kind whose
		///     magnitude is a percentage of another stat rather than a quantity in the target stat's own units: the
		///     record means "raise <c>StatId</c> by <c>WireAmount</c>% of <c>SourceStatId</c>". When the two ids are
		///     equal - 86 of the catalog's 106 elements - it degenerates to a plain percent of the stat itself, which
		///     is the 53067 model at a different scale (a plain percent here, where 53067 stores percent x100).
		/// </remarks>
		public readonly record struct Modifier(uint Kind, uint StatId, uint WireAmount, LevelRamp? Ramp = null, uint PeriodCentiseconds = 0, int? MaxAmount = null,
			LevelRamp? MaxRamp = null, IReadOnlyList<Criterion>? Criteria = null, uint? SourceStatId = null);

		/// <summary>
		///     One whole GameAction element: its type id and one value per key of that type's key list, in key order
		///     (<c>docs/re/gameactionlist-grammar.md</c>). <see cref="Value" /> addresses a slot by key, which is what
		///     makes a reader independent of where the slot sits - the stat is payload word 0 on <c>Modify</c> and
		///     word 1 on <c>SpellStatModifier</c>.
		///     <para>
		///         <paramref name="Offset" /> is the byte offset of the element's opening type word within the body
		///         that was scanned. It is what lets a caller measure a width instead of assuming one: the distance
		///         between two consecutive elements is that element's width, measured rather than predicted
		///         (<c>--report feat-effect-widths</c>).
		///     </para>
		///     <para>
		///         <paramref name="Fixed2d5" />/<paramref name="Fixed2d6" /> are the first two of the element's four
		///         fixed ints, carried because they are not padding: on an area-targeting kind <c>Fixed2d6</c> is the
		///         aura period in centiseconds (Gangrenous Stench's "every 4 seconds" is 400), and treating those
		///         words as a signature hides every aura.
		///     </para>
		/// </summary>
		public readonly record struct Element(
			int Offset,
			uint Kind,
			uint[] Values,
			string?[] Texts,
			int WidthBytes,
			uint Fixed2d5 = 0,
			uint Fixed2d6 = 0,
			uint[]? SubRecords = null)
		{
			/// <summary>
			///     The element's criteria as flat words, three per criterion - the region between the sub-record count
			///     and the four fixed ints. Empty when the element carries none. See <see cref="Criteria" /> for the
			///     typed view.
			/// </summary>
			public uint[] SubRecords { get; } = SubRecords ?? [];

			/// <summary>
			///     The element's criteria - the conditions under which it applies.
			/// </summary>
			/// <remarks>
			///     <para>
			///         The client names them. <c>FUN_00899a60</c>'s sub-record reader is <c>FUN_006e9f20</c>, which
			///         reads three u32s, bounds the third to <c>0..0x401</c>, and on failure logs "invalid criterion
			///         id/value/operator (" - so the triple is <c>[id][value][operator]</c>, in that order.
			///     </para>
			///     <para>
			///         This is what makes an apparent duplicate not one. Of the 1,117 elements whose decoded slots
			///         repeat another element of the same record, 987 differ only here - they are the same effect
			///         under different conditions, not the same effect stated twice. <c>AddProc</c> at
			///         <c>1006601@480</c> is the clean case: its twin's criteria are identical except that the
			///         operators <c>93</c> and <c>103</c> are swapped between two ids.
			///     </para>
			///     <para>
			///         The id space and the operator space are not decoded - nothing yet says what criterion 150 is or
			///         what operator 1 tests. Carried so a consumer can tell two elements apart; no engine branches on
			///         one.
			///     </para>
			/// </remarks>
			public IEnumerable<Criterion> Criteria
			{
				get
				{
					for (var i = 0; i + 2 < SubRecords.Length; i += 3)
						yield return new Criterion(SubRecords[i], SubRecords[i + 1], SubRecords[i + 2]);
				}
			}

			/// <summary>The registered type, or null when the kind is not a GameAction the client knows.</summary>
			public GameActionType? Type => GameActionTypes.Get(Kind);

			/// <summary>
			///     The value of the slot carrying <paramref name="key" />, or null when this type has no such slot. A
			///     text slot reports its byte length here, which is what is stored in the u32 - reach for
			///     <see cref="Text" /> for the string itself.
			/// </summary>
			public uint? Value(int key)
			{
				var slot = Type?.SlotOf(key) ?? -1;
				return slot >= 0 && slot < Values.Length ? Values[slot] : null;
			}

			/// <summary>
			///     The level ramp this element carries, or null when its type is not one of the interpolated family
			///     (<see cref="GameActionType.IsLevelRamp" />) or the body ran out before all four slots were read.
			///     The two values are signed - a spell ramp's low endpoint really is negative in the shipped data
			///     (-169), which reading them as <c>uint</c> turns into 4.29 billion.
			/// </summary>
			public LevelRamp? Ramp
			{
				get
				{
					if (Type is { IsInterpolatedHitRange: true })
						return HitRamp(GameActionTypes.KeyHitMinLow, GameActionTypes.KeyHitMinHigh);

					if (Type is not { IsLevelRamp: true })
						return null;

					var low = Value(GameActionTypes.KeyLowValue);
					var high = Value(GameActionTypes.KeyHighValue);
					var lowLevel = Value(GameActionTypes.KeyLowLevel);
					var highLevel = Value(GameActionTypes.KeyHighLevel);

					return low is null || high is null || lowLevel is null || highLevel is null
						? null
						: new LevelRamp((int)low.Value, (int)high.Value, lowLevel.Value, highLevel.Value);
				}
			}

			/// <summary>
			///     The magnitude's upper endpoint, for a type that states a range - <c>Hit</c>'s key 37, and the
			///     interpolated family's key 170 at the ramp's low level. Null for every type that states one number,
			///     which is what makes "this record has a max" a property of the key list rather than of a kind
			///     allowlist.
			/// </summary>
			public int? MaxAmount
			{
				get
				{
					if (Type is { IsInterpolatedHitRange: true })
						return (int?)Value(GameActionTypes.KeyHitMaxLow);

					return Type is { IsHitRange: true }
						? (int?)Value(GameActionTypes.KeyHitMax)
						: null;
				}
			}

			/// <summary>
			///     The ramp of the range's upper endpoint (170 @172 to 171 @173) - the twin of <see cref="Ramp" />,
			///     which carries the lower one. Non-null only on the interpolated hit family, whose record is a pair
			///     of ramps rather than one: <c>Gangrenous Stench</c> ticks for 58.40-71.40 at level 75 and
			///     62.20-76.10 at 80, and keeping only <see cref="Ramp" /> loses the whole upper edge of that.
			/// </summary>
			public LevelRamp? MaxRamp => Type is { IsInterpolatedHitRange: true }
				? HitRamp(GameActionTypes.KeyHitMaxLow, GameActionTypes.KeyHitMaxHigh)
				: null;

			// One of the interpolated hit family's two ramps: a value pair over the shared 172/173 levels. The values
			// are signed - a damage hit's endpoints are negative in the shipped data - which reading them as `uint`
			// turns into 4.29 billion.
			private LevelRamp? HitRamp(int lowKey, int highKey)
			{
				var low = Value(lowKey);
				var high = Value(highKey);
				var lowLevel = Value(GameActionTypes.KeyLowLevel);
				var highLevel = Value(GameActionTypes.KeyHighLevel);

				return low is null || high is null || lowLevel is null || highLevel is null
					? null
					: new LevelRamp((int)low.Value, (int)high.Value, lowLevel.Value, highLevel.Value);
			}

			/// <summary>
			///     The string in the slot carrying <paramref name="key" />, or null when this type has no such slot
			///     or that slot is not text (<see cref="GameActionTypes.TextKeys" />).
			/// </summary>
			public string? Text(int key)
			{
				var slot = Type?.SlotOf(key) ?? -1;
				return slot >= 0 && slot < Texts.Length ? Texts[slot] : null;
			}

			// The pair view a caller without the key lists uses: the first two payload words. A one-key element has no
			// second word, and reading one there picks up the next element's type id, which makes a `Spell` grant look
			// like it "repeats its own kind".
			internal uint First => Values.Length > 0 ? Values[0] : 0;
			internal uint Second => Values.Length > 1 ? Values[1] : 0;
		}

		/// <summary>
		///     One parsed kind-53054/53062 element. <paramref name="TargetId" /> is a <c>Spell.Id</c> under 53054 and
		///     a <c>Spell.GuiStackingLine</c> under 53062 - the kind is what says which.
		/// </summary>
		public readonly record struct SpellStatModifier(uint Kind, uint TargetId, CharacterStatId Stat, int? Value, int? PercentHundredths);

		/// <summary>The kind that scopes a stat modifier to one spell - its target is a <c>Spell.Id</c>.</summary>
		public const uint SpellStatModifierKind = 53054;

		/// <summary>The kind that scopes it to a whole spell line - its target is a <c>Spell.GuiStackingLine</c>.</summary>
		public const uint SpellLineStatModifierKind = 53062;

		/// <summary>
		///     The spell-scoped stat modifiers: kinds 53054 <c>SpellStatModifier</c> and 53062
		///     <c>SpellLineStatModifier</c>, the same four-slot payload with the target space swapped - "make spell X
		///     (or every rank of line L) crit harder, cost less mana, cast faster".
		/// </summary>
		/// <remarks>
		///     One mechanism, two sources, which is why this parser lives here rather than in either importer. A feat
		///     states these (<c>FeatSpellStatModifier</c>) and so does a spell (<c>SpellSpellStatModifier</c>) -
		///     identical kinds, identical key lists, identical payload - so a buff that makes your next Fireball crit
		///     harder and a feat that does the same are one record read from two sources.
		///     <para />
		///     Read by key, never by position - <c>(Spell|Line, Stat, Value, Amount)</c>. A pair view cannot decode
		///     these: <c>SpellStatModifier</c> puts the spell first and the stat second, so its "magnitude" is a stat
		///     id.
		///     <para />
		///     <c>Value</c> and <c>Amount</c> are alternatives, not a pair, and the client's own tooltip builder is
		///     the evidence: at 014a2714 it renders the <c>Value</c> slot through the generic stat formatter (which
		///     appends the stat's unit suffix) and the <c>Amount</c> slot through a copy that appends a hardcoded
		///     "%" - the literal at 0x01fbcf18, on all three of that branch's print paths. Exactly one of the two is
		///     set on all 1068 shipped records, so the pair is stored as two nullable columns rather than a value plus
		///     a mode flag.
		/// </remarks>
		// pair is stored as two NULLABLE columns rather than a value plus a mode flag.
		public static IReadOnlyList<SpellStatModifier> ParseSpellStatModifiers(byte[] body)
		{
			return ParseSpellStatModifiers(body, out _);
		}

		/// <summary>
		///     As <see cref="ParseSpellStatModifiers(byte[])" />, reporting how many elements were rejected as
		///     mis-sliced so the importer can say so rather than dropping them silently.
		/// </summary>
		public static IReadOnlyList<SpellStatModifier> ParseSpellStatModifiers(byte[] body, out int misSliced)
		{
			misSliced = 0;
			if (body.Length <= 12)
				return [];

			var mods = new List<SpellStatModifier>();
			foreach (var e in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
			{
				if (e.Kind != SpellStatModifierKind && e.Kind != SpellLineStatModifierKind)
					continue;

				// Every slot must be present: a key the type declares but the element does not carry means the element
				// was mis-sliced, and half a modifier is worse than none.
				var target = e.Value(e.Kind == SpellStatModifierKind ? GameActionTypes.KeySpell : GameActionTypes.KeyLine);
				var stat = e.Value(GameActionTypes.KeyStat);
				var value = e.Value(GameActionTypes.KeyValue);
				var amount = e.Value(GameActionTypes.KeyAmount);
				if (target is null or 0 || stat is null or 0 || value is null || amount is null)
					continue;

				// Both set, or neither, is a shape this decode does not cover - drop it rather than guess which wins.
				if (value == 0 == (amount == 0))
					continue;

				// A magnitude that is itself a registered GameAction type id is the next element's opening word, read
				// as ours because this element was narrower than its key list says. `Swift Pathing`
				// (5031395..5031401) is the case in the shipped catalog: its two 53054 elements tile the record end to
				// end at ten words each, so the `Value` slot lands on the following element's `0xCF3E`. Rejecting on
				// the type-id range is not a special case for that feat - a spell id, a stat id and a magnitude all
				// live nowhere near 0xCF09..0xD026, so a slot that does was never ours.
				if (GameActionTypes.Get(value.Value) != null || GameActionTypes.Get(amount.Value) != null)
				{
					misSliced++;
					continue;
				}

				mods.Add(new SpellStatModifier(e.Kind, target.Value, (CharacterStatId)stat.Value,
					value == 0 ? null : (int)value.Value, amount == 0 ? null : (int)amount.Value));
			}

			return mods;
		}

	}
}
