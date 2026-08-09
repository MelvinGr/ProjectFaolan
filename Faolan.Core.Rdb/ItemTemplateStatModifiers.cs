using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	// Parses the item template's stat-modifier region - the full tooltip stat list (attributes, vitals AND ratings),
	// distinct from the strictly-decreasing property table (which only carries the 972 primary attribute, value 74,
	// quality 499, bag 104). See docs/rdb/item-template-stats.md.
	//
	// Each modifier is a fixed 10-word (u32 LE) record, anchored on a 6-word signature:
	//     [kind][0][4][0][1][0][2][9][statId][amount]
	//               └────── 4,0,1,0,2,9 ──────┘
	// `statId` is a wire stat id (same space as the 0x96B8DC59 vitals / snapshot stat block); `amount` is the bonus at
	// WIRE SCALE (÷100 for vitals + Magic Damage, ÷10 for attributes, ÷1 for ratings - CharacterStatId.GetScale()).
	//
	// `kind` is the word that OPENS the record, and it is what makes the region heterogeneous: it is an effect-KIND tag
	// (a ~53000-range id), not padding. Item templates use one kind throughout, but SPELL templates - parsed by this
	// same code (#367) - mix several, and the kind is what says how to read `amount`:
	//     53045 / 53189 / 53220 / 53221   flat modifier (Battle Cry +62 Hit Rating, Fend +1000 Armor)
	//     53067                           PERCENT modifier, ×100 (Philtre of Guile 200 = +2% Critical Rating)
	//     53219 / 53002 / 53007 / 53013   health effects - heals and the -100000000 drain/kill sentinel
	// So a consumer MUST branch on the kind: reading a 53067 record as flat turns "+2%" into "+200". The classification
	// above is empirical (docs/rdb/spell-stat-effects.md); the server honors only what it can justify - see
	// BuffStatEffects.
	//
	// ANCHOR ON THE SIGNATURE, not a raw [2][9] scan: the raw scan yields ~2500 hits (a third of them coincidental
	// 2,9 word pairs); the signature yields the ~1550 real records (the word before the `4` is 0 in 100% of them).
	// Even so a few signature hits carry an RDB resource-type id as statId (1000017/1000018) or garbage (0x3de071,
	// 0xd00d) - so we bound statId to the real stat-id range (0, 0x1000). Validated 3/3 against the in-client Royal
	// Signet tooltip (+30 Con / +52 Magic Damage / +15 Hit Rating; template 3774542).
	public static class ItemTemplateStatModifiers
	{
		// Stat ids are small (the largest real one is ~0x35d); this bound drops the resource-ref / garbage signature
		// hits without needing a full whitelist here (the importer whitelists to the stats it applies).
		private const uint MaxStatId = 0x1000;

		// All stat-modifier records in a decrypted template BODY (the span AFTER the 12-byte RDB-Id header), in file
		// order. Signature-anchored; statId bounded to the real range. Duplicates are preserved (a template never
		// repeats a stat id in the corpus, but the parser stays faithful to what's on the wire).
		public static IReadOnlyList<Modifier> Parse(ReadOnlySpan<byte> body)
		{
			return Scan(body, true);
		}

		/// <summary>
		///     The records <see cref="Parse" /> REJECTS - those whose payload's first word is outside the stat-id
		///     range. That bound is right for a stat modifier and wrong for the rest: a FEAT's spell modifier puts a
		///     spell-LINE id there and the magnitude second, so `Decomposition` rank 5 reads (41000, 3500) = "+35.00%
		///     on Necromancer line 0, Flesh to Worms" (#590). Same scan, opposite filter - deliberately not a second
		///     copy of the signature walk, so the two can never disagree about where a record IS, only about which
		///     ones count.
		///     <para>
		///         The KIND is what says how to read one, and the kinds are NOT anonymous: they are GameAction type
		///         ids, catalogued in <c>GameActionTypes</c> with their key lists. <b>53075</b> is
		///         <c>ChangeLineHitPercent</c> (a feat's spell modifier - see <c>RdbFeatImporter.ParseSpellModifiers</c>
		///         and `--report feat-spell-modifiers`).
		///     </para>
		///     <para>
		///         <b>53051 is <c>CastNano</c></b> (key 39) - "nano" being Funcom's Anarchy Online word for a spell.
		///         On an ITEM template its payload is a <c>Spells.Id</c>: the spell the item casts when used. That is
		///         the source <c>Item.OnUseSpell</c> wanted; the kind was already named here, nobody had connected an
		///         item's CastNano element to the on-use effect. Measured over 26 dumped templates (12 with an on-use
		///         spell harvested from captures, 14 without): 8 of the 12 controls recovered exactly, each resolving
		///         to the right spell NAME - Raw Beets 112936 "Hearty Meal", Daggamalt 180250 "Health Potion Effect
		///         10", Double XP 4552864 "Double XP" - with ZERO false positives across the 14. Of the 4 misses both
		///         loot caches carry no spell in the template at all, Totem of Origins carries a different spell than
		///         the capture recorded, and Moonspill carries its under another prefix. Not imported yet - Gitea #762.
		///     </para>
		/// </summary>
		public static IReadOnlyList<Modifier> ParseRejected(ReadOnlySpan<byte> body)
		{
			return Scan(body, false);
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
			// ⚠️ Scan BYTE by byte, not word by word (#441). The region is NOT 4-byte aligned to the body in most
			// templates - Blackwolf Cap's records start at body offset 297 - and a word-aligned scan simply cannot see
			// those, silently returning zero records. That was 2873 of 3248 templates reporting "no stats" while the
			// client happily applied a full list from the very same bytes. Byte-granular scanning recovers them exactly
			// (Blackwolf Cap 0 -> 4 records, Con 50 / Int 50 = what the client applies) and changes nothing for the
			// templates that were already aligned (Royal Signet stays 3, Fine Steelsilk Pants stays 6).
			for (var off = 4; off + 24 <= body.Length; off++)
			{
				if (W(body, off - 4) != 0 || W(body, off) != 4 || W(body, off + 4) != 0 || W(body, off + 8) != 1 || W(body, off + 12) != 0 || W(body, off + 16) != 2 ||
				    W(body, off + 20) != 9)
					continue;

				var kind = off >= 8 ? W(body, off - 8) : 0;
				// How many values follow is a property of the KIND, not a constant: an element carries exactly one
				// u32 per key-list entry (docs/re/gameactionlist-grammar.md). An unregistered kind gets the two the
				// old fixed-10-word reader assumed, which is all any caller of the pair readers below ever used.
				var type = GameActionTypes.Get(kind);

				// The payload is walked SLOT BY SLOT rather than indexed, because a TEXT slot is `[u16 byteLength]
				// [bytes]` and so shifts every slot after it - and by an ODD number of bytes, which is why a
				// text-carrying element has no fixed width and why a positional read past one reported the string's
				// LENGTH as the next slot's value (Gitea #596). `SetMorphValue`'s "mesh name after the element" was
				// this slot all along.
				//
				// The u16 is the client's own string primitive, and the only one on this path: the deserializer
				// `FUN_00899a60` takes the string branch for exactly `GameActionStat_e` 0x4d9/0x4da and calls
				// `FUN_0048f840` -> `FUN_00cbf840` mode 0, which reads a u16 and treats >= 0x8000 as an escape into a
				// long form; the WRITER `FUN_00899440` emits the same u16 with the same escape, and neither branches
				// on the type. It is also what the shipped records measure: of the 880 text-carrying elements in the
				// feat and spell catalogs, 653 tile EXACTLY under this reading (`--report feat-effect-kinds`), and the
				// strings come out whole - "spell_curseofflies" as a 0x12-byte slot, "arm" as a 3-byte one, neither
				// carrying a NUL.
				//
				// ⚠️ 7 elements do NOT - all of them `HeadText`, reading `[u32 7]["Dimach\0"][u32 3]` two bytes wider
				// than the u16 form allows. Deliberately still not modelled: they are RETIRED DATA (#596), sitting in
				// the same low-id feat templates (3753, 12362..12386) that carry the retired kind 53044 and a
				// `ChangeHitPercent` padded with terminator words, while every u16 record in either catalog is
				// template id 791547 or higher. A second length width here would be a tolerant reader for bytes the
				// client itself refuses to stream.
				//
				// An UNREGISTERED kind has no key list, so it keeps the two u32 slots the pair readers below need and
				// nothing more. That is not a decode gap waiting to be filled: the catalog covers every id the client
				// registers, and the ids it does not are RETIRED (GameActionTypes.UnregisteredIds), so an element
				// carrying one is data the client refuses to stream at all.
				var keys = type?.Keys ?? [GameActionTypes.KeyStat, GameActionTypes.KeyValue];
				var values = new List<uint>(keys.Length);
				var texts = new List<string?>(keys.Length);
				var at = off + 24;
				foreach (var key in keys)
				{
					// Read as many slots as the body HOLDS, not as many as the key list wants. Where an element is
					// followed by too few bytes for its full payload, dropping it would hide the one thing worth
					// seeing: an element narrower than its type says (Gitea #595). It is still reported - with a
					// short value array, so `Value(key)` returns null for the slots that are not there and every
					// caller that needs a whole modifier rejects it on its own terms rather than on this scan's
					// silence.
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

				elements.Add(new Element(off - 8, kind, [.. values], [.. texts], at - (off - 8)));
			}

			return elements;
		}

		/// <summary>
		///     The records that really are a modifier of the CARRIER's own stats, read BY KEY rather than by position
		///     (Gitea #597). Three conditions, each of them a property of the type's key list rather than of the
		///     bytes:
		///     <list type="number">
		///         <item>it has a <c>Stat</c> slot (key 0) - `NanoResistance` (152, Value), `DisplayLearnAbility`
		///         (Line, 187) and `AddPercentSkillModifier` (66, SkillType, 67, Value) have none, so the pair view's
		///         first word was a strain / an ability ref / an unnamed slot filed as a stat id;</item>
		///         <item>it has a magnitude - <c>Value</c> (39), or the interpolated family's low endpoint (174) - so
		///         a pure damage record (`Hit` is Stat + three unnamed slots) is not mistaken for a bonus;</item>
		///         <item>it carries NO <c>Spell</c> (88) or <c>Line</c> (1) slot, because such a record modifies that
		///         SPELL's properties rather than the carrier's stats. That is the whole of `SpellStatModifier` /
		///         `SpellLineStatModifier` / `TargetSpellLineStatModifier`, which have their own table (#593).</item>
		///     </list>
		///     <para>
		///         ⚠️ For the INTERPOLATED kinds <c>WireAmount</c> is the value at the ramp's LOW level, not a
		///         level-independent bonus: the record is `lerp(low@lowLevel, high@highLevel)` over the carrier's
		///         level (docs/re/gameactionlist-grammar.md). Applying it alone under-grants everyone above that
		///         level, so <see cref="Modifier.Ramp" /> carries the whole ramp beside it - resolve it through
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

				mods.Add(new Modifier(e.Kind, stat.Value, amount.Value, e.Ramp));
			}

			return mods;
		}

		/// <summary>
		///     The stat effects a template's region carries, read BY KEY rather than by POSITION (#701).
		/// </summary>
		/// <remarks>
		///     <para>
		///         ⚠️ This is what <see cref="Parse" /> should always have been for the spell catalog. The pair view
		///         returns payload words 0 and 1 <b>whatever keys they belong to</b>, and that is only (stat, amount)
		///         for the 12 kinds whose key list opens with key 0. For the other 41 it silently files another
		///         field's value as a stat id: `NanoResistance` `[152, 39]` reported its key-152 word as
		///         `Team`/`State`/`SellModifier`, `SetSpellLineRecastTime` `[1, 39]` reported a spell LINE as a stat,
		///         and `StartParticleEffect` `[209, …]` reported the BYTE LENGTH of a string. 1,200 of the 6,133
		///         shipped rows were that.
		///     </para>
		///     <para>
		///         The MAGNITUDE is the first key AFTER the stat in the type's own key list, which is one rule that
		///         gets all twelve right: key 39 `Value` on `Modify`, key 2 on `Hit`, key 168 on `InterpolatedHit`,
		///         key 174 (the ramp's low endpoint) on `InterpolatedModify`, key 70 on `AddAbsorb`, and key 39 again
		///         on `Set`, whose stat is at slot 1 rather than slot 0. It is also exactly what the positional read
		///         was doing by accident wherever the stat happens to come first - so this changes no VALUE that was
		///         already right, it only stops inventing the ones that were not.
		///     </para>
		/// </remarks>
		public static IReadOnlyList<Modifier> ParseSpellStatEffects(ReadOnlySpan<byte> body)
		{
			var mods = new List<Modifier>();
			foreach (var e in ParseElements(body))
			{
				// An UNREGISTERED kind has no key list, so nothing says where its stat is - and the catalog covers
				// every id the client registers, which makes an unregistered one retired data rather than a gap.
				if (e.Type is not { } type)
					continue;

				var statSlot = type.SlotOf(GameActionTypes.KeyStat);
				if (statSlot < 0 || statSlot + 1 >= type.Keys.Length)
					continue; // this kind carries no stat, or no slot after it to hold a magnitude

				var stat = e.Value(GameActionTypes.KeyStat);
				var amount = e.Value(type.Keys[statSlot + 1]);
				if (stat is null or 0 or >= MaxStatId || amount is null)
					continue;

				mods.Add(new Modifier(e.Kind, stat.Value, amount.Value, e.Ramp));
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

				mods.Add(new Modifier(e.Kind, statId, e.Second, e.Ramp));
			}

			return mods;
		}

		// Reads a u32 at a BYTE offset - deliberately not word-indexed, see the scan note in Parse.
		private static uint W(ReadOnlySpan<byte> body, int byteOffset)
		{
			return body.ReadU32Le(byteOffset);
		}

		// One record: its effect KIND (the opening word - see the type comment), the wire stat id, and the amount as it
		// sits in the template (NOT yet scaled). Divide by CharacterStatId.GetScale() for display - after checking that
		// the kind means "flat".
		//
		// ⚠️ This is the PAIR view: the first two payload words, which is (stat, value) for the plain modifier kinds
		// and something else for the rest - a `SpellStatModifier` puts the SPELL first and the stat second. Reach for
		// <see cref="Element" /> when the kind is not known to be a plain modifier.
		//
		// `Ramp` is non-null exactly for the INTERPOLATED kinds, whose `WireAmount` is only the ramp's LOW endpoint -
		// a consumer that applies it flat under-grants everyone above that level (Gitea #599). It carries the whole
		// ramp, low endpoint included, so a caller never has to pair it back up with `WireAmount` (and cannot get the
		// signedness wrong doing so: the low value is signed and `WireAmount` is not).
		public readonly record struct Modifier(uint Kind, uint StatId, uint WireAmount, LevelRamp? Ramp = null);

		/// <summary>
		///     One whole GameAction element: its type id and one value per key of that type's key list, in key order
		///     (docs/re/gameactionlist-grammar.md). <see cref="Value" /> addresses a slot BY KEY, which is what makes
		///     a reader independent of where the slot sits - the stat is payload word 0 on `Modify` and word 1 on
		///     `SpellStatModifier`.
		///     <para>
		///         <paramref name="Offset" /> is the byte offset of the element's opening TYPE word within the body
		///         that was scanned. It is what lets a caller measure a width instead of assuming one: the distance
		///         between two consecutive elements is that element's width, measured rather than predicted
		///         (`--report feat-effect-widths`, Gitea #595).
		///     </para>
		/// </summary>
		public readonly record struct Element(int Offset, uint Kind, uint[] Values, string?[] Texts, int WidthBytes)
		{
			/// <summary>The registered type, or null when the kind is not a GameAction the client knows.</summary>
			public GameActionType? Type => GameActionTypes.Get(Kind);

			/// <summary>
			///     The value of the slot carrying <paramref name="key" />, or null when this type has no such slot.
			///     A TEXT slot reports its BYTE LENGTH here, which is what is stored in the u32 - reach for
			///     <see cref="Text" /> for the string itself.
			/// </summary>
			public uint? Value(int key)
			{
				var slot = Type?.SlotOf(key) ?? -1;
				return slot >= 0 && slot < Values.Length ? Values[slot] : null;
			}

			/// <summary>
			///     The LEVEL RAMP this element carries, or null when its type is not one of the interpolated family
			///     (<see cref="GameActionType.IsLevelRamp" />) or the body ran out before all four slots were read.
			///     The two values are SIGNED - a spell ramp's low endpoint really is negative in the shipped data
			///     (`-169`), which reading them as `uint` turns into 4.29 billion.
			/// </summary>
			public LevelRamp? Ramp
			{
				get
				{
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
			///     The string in the slot carrying <paramref name="key" />, or null when this type has no such slot
			///     or that slot is not text (<see cref="GameActionTypes.TextKeys" />).
			/// </summary>
			public string? Text(int key)
			{
				var slot = Type?.SlotOf(key) ?? -1;
				return slot >= 0 && slot < Texts.Length ? Texts[slot] : null;
			}

			// The pair view every caller predating the key lists used: the first two payload words. A one-key element
			// has no second word - it used to read the NEXT element's type id, which is how a `Spell` grant came to
			// look like it "repeats its own kind" (#591).
			internal uint First => Values.Length > 0 ? Values[0] : 0;
			internal uint Second => Values.Length > 1 ? Values[1] : 0;
		}
	}
}
