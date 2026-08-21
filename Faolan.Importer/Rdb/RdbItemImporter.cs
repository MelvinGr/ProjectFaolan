using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Decrypts the client RDB item-template for every item we hold a key for. The TEA key is the item's
	// ProtoDecryptionKey - the same 4 u32 the server streams in inventory and we store as Item.Key (Key1..4) (see
	// docs/rdb/rdb-extraction.md "RDBH cracked"). RDB type 1000020 (item) / 1000620 (recipe), id = Item.Id.
	// The decrypted payload is the full canonical template (12-byte RDB-Id header + binary fields); reversing those
	// fields into stat columns is the next step, so for now this validates the decrypt end-to-end and (optionally)
	// dumps the templates for analysis.
	public static class RdbItemImporter
	{
		private const uint BagPropertyField = 104; // = ContainerKind.Bag (0x68)

		// Property field 0 is the client's `Flags` word - the item's birth stat-0 value (#756). Bit 26 is BOUND, so a
		// template carrying it mints items that are already bound: the client's "Binds when Picked Up".
		private const uint FlagsPropertyField = 0;

		// The two fields the client's bind-on-EQUIP predicate reads besides the quality tier (field 499, which is
		// already Item.Quality): 177 "More Flags" and 30 "Can". See Item.BindsOnEquip.
		private const uint MoreFlagsPropertyField = 177;
		private const uint CanPropertyField = 30;

		// `NumGemSlots` - how many gems the item can take (#754).
		private const uint GemSlotsPropertyField = 114;
		private const uint MarkerItem = 1000000;

		// Bag capacity = property field 104 (= the Bag container kind, 0x68) for the normal backpack bag, or field 1331
		// for a RESOURCE bag. Unlike Value/attributes these sit in the property table's non-monotonic tail, so
		// TryReadPropertyField (strictly-decreasing) can't reach them. Scan for the first [fieldId][value] u32 LE pair
		// whose value is a plausible slot count (1..512). Validated: the normal bags (Burlap 24 / Bag of Holding 20 /
		// Veteran's Bag 35 / Max Inventory 216) via 104, and the Max Resource Bag (102) via 1331. Non-bag templates
		// virtually never carry these fields, and the value is only ever used for an item in the matching bag slot, so
		// a stray match is inert.
		// The highest field-499 value: it is 0-indexed (0=Mundane .. 5=Legendary), so the max is the number of tiers
		// above Mundane = Legendary - Mundane = 5.
		private const uint MaxQualityFieldValue = (uint)ItemQuality.Legendary - (uint)ItemQuality.Mundane;
		private const uint ResourceBagPropertyField = 1331; // the resource-bag capacity field (no ContainerKind analogue)

		// String fields inside a template are encoded `tag 0x21 (u32) + length (u16) + flags (u16) + <length> ASCII
		// bytes`. The length is a u16, NOT a u32 - the next u16 is a flags word that is nonzero whenever the string
		// carries backslash-escaped chars (e.g. an apostrophe `\'`). Reading the length as a u32 folds that flags word
		// into the high bytes, so names like "Aspirant's Cap" came out as a bogus 1.5M length and got skipped (#202) -
		// the ~900 names that worked all happened to have flags == 0. The first such string is the display name;
		// internal asset names (mesh refs like `NPC_..._(W0129)`) contain underscores, so we skip those and take the
		// first underscore-free string. NPC/TEST/internal items carry only an asset name and have no display name - we
		// leave their captured (empty) name untouched.
		private const uint StringTag = 0x21;

		// The [2][9]-region stats we import + apply today: vitals (the #3 health-bar-fill headline), the five
		// attributes, and the combat ratings that now HAVE a consumer (Armor/Protection mitigation #401, Hit/Crit/
		// CombatRating + Magic Damage + regen #399, all read off gearBonuses in WorldSimulation.RegisterPlayerInWorld).
		// The region's other ~30 unnamed rating ids stay unimported until a consumer lands. NB the five attributes are
		// imported for server-side derived-stat use, NOT to patch the sent stat block: retail sends BASELINE attributes
		// (Str=Int=10) in the snapshot for every char L1-L80 and NEVER updates them live (verified across every login
		// capture + a level-up) - the client derives displayed attributes itself from class/level/feats + worn gear, so
		// patching gear totals onto the wire would diverge from retail and double-count (#399 item 1, won't-do).
		// The GameAction element that carries an item's on-use spell: `e_Type_GC_GameAction_CastNano` (key list [39]).
		// "Nano" is Funcom's Anarchy Online word for a spell. See ItemTemplateStatModifiers and Gitea #762.
		private const uint CastNanoKind = 53051;
		private const int CastNanoSpellKey = 39;

		// The element that gives a consumable its SHARED cooldown (#764): `CoreLockSpellLineTemplate`, key 39 = the
		// spell LINE to lock and key 66 = for how long. The line groups by the resource restored - health 80000,
		// stamina 80100, mana 80200 - so the three lock independently.
		//
		// ⚠️ Not to be confused with kind 53116 `LockSpellLine`, which ships with an EMPTY key list. Whatever that
		// variant is, it is not this one read differently.
		private const uint LockSpellLineKind = 53124;
		private const int LockSpellLineKey = 39;
		private const int LockSpellLineDurationKey = 66;

		// Internal rather than private so `--report item-effect-kinds` can measure the import against the SAME
		// whitelist the import applies - a census run against a different set would answer a different question.
		internal static readonly HashSet<uint> ApplicableStatIds =
		[
			(uint)CharacterStatId.HealthMax, (uint)CharacterStatId.StaminaMax, (uint)CharacterStatId.ManaMax, (uint)CharacterStatId.Strength, (uint)CharacterStatId.Constitution,
			(uint)CharacterStatId.Intelligence, (uint)CharacterStatId.Wisdom, (uint)CharacterStatId.Dexterity,
			(uint)CharacterStatId.Armor, // #401: physical armor rating, applied as incoming-damage mitigation
			(uint)CharacterStatId.HitRating, // #399: drives the provisional critical-hit chance
			(uint)CharacterStatId.MagicDamage, // #399: added flat to the caster's spell damage
			(uint)CharacterStatId.NaturalManaRegen, // gear mana regen (added to the regen tick)
			(uint)CharacterStatId.NaturalStaminaRegen, // gear stamina regen (added to the regen tick)
			(uint)CharacterStatId.CriticalRating, // #399: melee crit chance
			(uint)CharacterStatId.CriticalDamageRating, // #399: melee crit magnitude
			(uint)CharacterStatId.Protection, // #401: magical mitigation of incoming NPC-cast damage
			(uint)CharacterStatId.CombatRating // #399: provisional flat melee damage bonus
		];

		// The ONE effect kind whose payload is a flat additive stat delta, and so the only one `ItemStatModifier` can
		// hold: `MaxStatAggregator.AggregateGearBonuses` sums the column straight into the gear total, and the table
		// carries neither a kind nor the ramp columns `FeatEffect` grew for #599. Measured, not assumed - see the
		// import loop's remarks and `--report item-effect-kinds` (#806).
		private const uint FlatModifierKind = 53045; // `Modify` [Stat, Value]

		private static readonly uint[] ItemTemplateTypes = [1000020, 1000620];

		// The item-template type that carries NO 12-byte RDB-Id header, and whose body therefore starts at 0.
		private const uint UnheaderedTemplateType = 1000620;

		// An item template's own TEXT: its display name and, when it has one, its tooltip description (#891).
		//
		// The two template types frame it DIFFERENTLY, which is why one reader could never see both:
		//
		//   1000020  `[u32 0x21][u16 nameLen][u16 descLen][name][description]` - both lengths up front, the strings
		//            back to back. `Animated Dagger` carries 0x0F and 0x20 there, then its 15- and 32-byte strings.
		//            An item with no description carries descLen 0 (`Dusklight Tunic`, `Combatant's Hauberk`).
		//   1000620  `[u16 nameLen][name][u16 descLen][description]` - NO tag at all, each length in front of its own
		//            string, and the block ENDS the record. The Self-Resurrection Potion's 24-byte name is followed by
		//            a 337-byte description that runs to the last byte of the body.
		//
		// ⚠️ The `[u16 descLen]` of the tagged form was read as "a flags word (escaping marker) we don't need" for as
		// long as only the NAME was wanted. It is a length, and reading it is the whole of this decode.
		//
		// Asset paths share the record (`armor_..._body`, `*bodymesh_NNN`), so an entry containing `_` is rejected:
		// no captured item name or description carries one.
		public static (string? Name, string? Description) ExtractText(ReadOnlySpan<byte> template, bool headered = true)
		{
			// A 1000620 record has no RDB-Id header, so its body IS the blob; a 1000020 one starts after the 12 bytes.
			var body = !headered
				? template
				: template.Length > 12
					? template[12..]
					: default;

			return TryTagged(body, out var name, out var description) || TryUntagged(body, out name, out description)
				? (name, description)
				: (null, null);
		}

		public static string? ExtractName(ReadOnlySpan<byte> template)
		{
			return ExtractText(template).Name;
		}

		// `[u32 0x21][u16 nameLen][u16 descLen]` then the two strings, back to back.
		//
		// ⚠️ A rejected field is stepped OVER, not scanned through. Fields are not 4-byte aligned - an asset name of
		// 31 bytes puts the next tag at offset 39 - so a plain `i += 4` sweep walks straight past every field that
		// follows one, which is exactly how an item whose asset path precedes its name loses its name.
		private static bool TryTagged(ReadOnlySpan<byte> body, out string? name, out string? description)
		{
			name = null;
			description = null;
			var i = 0;
			while (i + 8 <= body.Length)
			{
				if (body.ReadU32Le(i) == StringTag)
				{
					var nameLength = body.ReadU16Le(i + 4);
					var descriptionLength = body.ReadU16Le(i + 6);
					if (nameLength is >= 1 and <= 1024 && i + 8 + nameLength + descriptionLength <= body.Length && IsPrintableAscii(body.Slice(i + 8, nameLength)))
					{
						if (TryText(body.Slice(i + 8, nameLength), out var candidate))
						{
							name = candidate;
							if (descriptionLength > 0 && TryText(body.Slice(i + 8 + nameLength, descriptionLength), out var text))
								description = text;

							return true;
						}

						// A printable field we do not want (an asset path): skip the whole thing and keep looking.
						i += 8 + nameLength + descriptionLength;
						continue;
					}
				}

				i += 4;
			}

			return false;
		}

		// `[u16 nameLen][name][u16 descLen][description]`, ENDING the record - which is what makes this readable
		// without a tag to anchor on: the pair is accepted only when it lands exactly on the last byte, so a property
		// value that happens to look like a length cannot match.
		private static bool TryUntagged(ReadOnlySpan<byte> body, out string? name, out string? description)
		{
			name = null;
			description = null;
			for (var i = 0; i + 2 < body.Length; i++)
			{
				var nameLength = body.ReadU16Le(i);
				if (nameLength is < 1 or > 1024 || i + 2 + nameLength > body.Length)
					continue;

				if (!TryText(body.Slice(i + 2, nameLength), out var candidate))
					continue;

				var next = i + 2 + nameLength;
				if (next == body.Length)
				{
					name = candidate;
					return true;
				}

				if (next + 2 > body.Length)
					continue;

				var descriptionLength = body.ReadU16Le(next);
				if (next + 2 + descriptionLength != body.Length || !TryText(body.Slice(next + 2, descriptionLength), out var text))
					continue;

				name = candidate;
				description = text;
				return true;
			}

			return false;
		}

		private static bool TryText(ReadOnlySpan<byte> raw, out string? text)
		{
			text = null;
			if (raw.Length == 0 || !IsPrintableAscii(raw))
				return false;

			var candidate = Unescape(Encoding.ASCII.GetString(raw));
			if (candidate.Contains('_'))
				return false;

			text = candidate;
			return true;
		}

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? dumpDir = null, string? cdnBase = null)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);

			// (type, id) -> entry for the template types, skipping empty/sentinel entries. CDN-only (file==0xFF)
			// templates are kept when a mirror is reachable - RdbReader.ReadDecrypted pulls their blob through it.
			var byTypeId = new Dictionary<(uint, uint), RdbReader.Entry>();
			foreach (var type in ItemTemplateTypes)
			foreach (var e in rdb.EntriesOfType(type))
			{
				if (e.Size > 0 && rdb.CanRead(e))
					byTypeId[(type, e.Id)] = e;
			}

			var items = await db.Items.Include(i => i.StatModifiers).Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0).ToListAsync();

			// The on-use spell (below) is an FK to Spells, so a template naming a spell our catalog lacks must be
			// SKIPPED rather than stored - an unresolvable id would fail the constraint, and silently pointing at
			// nothing would be worse than leaving the item inert.
			var knownSpells = (await db.Spells.Select(sp => sp.Id).ToListAsync()).ToHashSet();

			if (dumpDir != null)
				Directory.CreateDirectory(dumpDir);

			int decrypted = 0,
				noEntry = 0,
				headerMismatch = 0,
				decryptFailed = 0,
				namesAdded = 0,
				descriptionsAdded = 0,
				categoriesAdded = 0,
				valuesAdded = 0,
				qualitiesAdded = 0,
				statsAdded = 0,
				bagsAdded = 0,
				flagsAdded = 0,
				bindFieldsAdded = 0,
				gemSlotsAdded = 0,
				weaponDamageAdded = 0,
				attackRangeAdded = 0,
				onUseAdded = 0,
				lockLinesAdded = 0,
				onUseUnknownSpell = 0;
			var firstSampleShown = false;

			// Every effect kind that landed on a whitelisted stat but is NOT a flat additive delta, by kind. Empty on
			// the shipped catalog (#806) - it exists to stay empty loudly.
			var nonFlatKinds = new Dictionary<uint, int>();

			foreach (var item in items)
			{
				var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };

				RdbReader.Entry entry = default;
				uint matchedType = 0;
				foreach (var type in ItemTemplateTypes)
				{
					if (byTypeId.TryGetValue((type, item.Id), out entry))
					{
						matchedType = type;
						break;
					}
				}

				if (matchedType == 0)
				{
					noEntry++;
					continue;
				}

				byte[] template;
				try
				{
					template = rdb.ReadDecrypted(entry, key);
				}
				catch
				{
					decryptFailed++;
					continue;
				}

				// ⚠️ Only the 1000020 records carry the 12-byte RDB-Id header - and this used to validate it on BOTH
				// types, so every 1000620 template was thrown away as a header mismatch (#891). Their first three
				// words are data (the Self-Resurrection Potion's read 5 / 30029 / 1), the strings sit at a different
				// offset, and the property table this loop reads at +12 is not there at all. There are 271 of them
				// against 98,048 of the other, which is why the loss stayed invisible: it costs a handful of items
				// their whole row, not a field on many.
				//
				// So they take a TEXT-ONLY path: their name and description are recoverable (the string block
				// self-anchors on the end of the record), and nothing else in this loop applies to their layout.
				if (matchedType == UnheaderedTemplateType)
				{
					decrypted++;
					var (unheaderedName, unheaderedDescription) = ExtractText(template, headered: false);
					if (string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrEmpty(unheaderedName))
					{
						item.Name = unheaderedName;
						namesAdded++;
					}

					if (string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrEmpty(unheaderedDescription))
					{
						item.Description = unheaderedDescription;
						descriptionsAdded++;
					}

					continue;
				}

				// Validate the 12-byte RDB-Id header: type + id must match what we asked for, marker == 1000000.
				if (template.Length < 12 || template.ReadU32Le(0) != matchedType ||
				    template.ReadU32Le(4) != item.Id || template.ReadU32Le(8) != MarkerItem)
				{
					headerMismatch++;
					continue;
				}

				decrypted++;

				// The AH search CATEGORY is the first u32 of the template body (right after the 12-byte RDB-Id header).
				// Validated byte-for-byte against 170 captured AH listings (#181). Lets a player's listing be filed
				// under the item-type search node the client queries by, for EVERY item - not just captured ones.
				var category = template.ReadU32Le(12);
				if (category != item.SearchCategory)
				{
					item.SearchCategory = category;
					categoriesAdded++;
				}

				// The template's `Flags` word - property field 0, the item's BIRTH stat-0 value, whose bit 26 is the
				// BOUND flag (#756). Read through ItemTemplateProperties, which walks the WHOLE property table:
				// TryReadPropertyField below cannot reach field 0 at all (it takes the longest strictly-decreasing run
				// and rejects fieldId 0, and field 0 lives at the end of the FIRST run). Measured against the dumps,
				// 174 of 179 captured DispatchAddItem arrivals carry exactly this value.
				var properties = ItemTemplateProperties.Parse(template.AsSpan(12));
				if (properties.Fields.TryGetValue(FlagsPropertyField, out var flags) && flags != item.Flags)
				{
					item.Flags = flags;
					flagsAdded++;
				}

				// The other two fields the client's bind-on-EQUIP predicate reads, stored raw so Item.BindsOnEquip
				// stays a derivation over template data rather than a conclusion frozen at import. Absent reads as 0,
				// which is what the client's own getProp returns for a field a template does not carry.
				var moreFlags = properties.Fields.GetValueOrDefault(MoreFlagsPropertyField);
				var can = properties.Fields.GetValueOrDefault(CanPropertyField);
				if (moreFlags != item.MoreFlags || can != item.Can)
				{
					item.MoreFlags = moreFlags;
					item.Can = can;
					bindFieldsAdded++;
				}

				// How many gem sockets the item has - `NumGemSlots`, what bounds a socketing request (#754).
				var gemSlots = properties.Fields.GetValueOrDefault(GemSlotsPropertyField);
				if (gemSlots != item.GemSlots)
				{
					item.GemSlots = gemSlots;
					gemSlotsAdded++;
				}

				// The item base VALUE = property-table field 74 (validated: vendor sell = round(value/10) per unit
				// against the captured sell anchors, #137). The property table is the run of [fieldId][value] u32 pairs
				// with strictly-decreasing fieldId after the body header (same parse as tools/capture/decode_item_template.py).
				// Some templates' tables don't carry field 74 (left at 0 -> the shop falls back to the level price).
				if (TryReadPropertyField(template.AsSpan(12), 74, out var value) && value != item.Value)
				{
					item.Value = value;
					valuesAdded++;
				}

				// The item QUALITY/rarity tier = property field 499 - the client's 0-indexed tooltip tier (0=Mundane ..
				// 5=Legendary), stored directly as ItemQuality (whose values ARE those ids). The strict-decreasing
				// property parse reaches 499 for most gear, but on some templates it sits OUTSIDE the main decreasing
				// run (e.g. the max-capacity bags carry 499=5 at a position the strict parser can't reach - the same
				// blind spot bag slots hit, #397), so fall back to a direct field scan.
				var found = TryReadPropertyField(template.AsSpan(12), 499, out var qualityRaw) && qualityRaw <= MaxQualityFieldValue;
				if (!found)
					found = TryReadQualityAnywhere(template.AsSpan(12), out qualityRaw);
				if (found && item.Quality != (ItemQuality)qualityRaw)
				{
					item.Quality = (ItemQuality)qualityRaw;
					qualitiesAdded++;
				}

				// The item's equipped stat bonuses (docs/rdb/item-template-stats.md, #3), as ItemStatModifier rows keyed
				// (ItemId, StatId). ONE decoded source: the [2][9] stat-modifier region
				// (ItemTemplateStatModifiers.Parse) - the whole tooltip stat list (attributes, vitals, ratings) at wire
				// scale, whitelisted to the stats we can name (ApplicableStatIds).
				//
				// ⚠️ There used to be a second source here: field 972 (`Weapon Attribute`, a stat id as its VALUE) with
				// field 971 read as its amount, applied when the region parsed to nothing. **971 is not 972's amount**
				// (#631). 971/972/973 are the client's own coherent TRIPLE - `Weapon Critical Chance` /
				// `Weapon Attribute` / `Weapon Critical Damage` - and the client PROVES 971 is a percent: its
				// unit-suffix helper (FUN_01119f20) appends "%" for exactly the stat ids listed in it, and 971 and 973
				// are both in that list while 972 is not. A crit chance is not a "+1 Strength", so the fallback was
				// emitting a bonus the client never applies, on templates whose region is empty.
				//
				// Nothing replaces it. 972 names an attribute but states no amount (it reads like the spell record's
				// ScalingAttribute - what the weapon scales OFF, not a bonus it grants), and 971/973 are crit stats
				// whose wire scale is unmeasured, so emitting them would be inventing a number. Tracked in #631.
				//
				// ⭐ Read BY KEY (`ParseSpellStatEffects`), not by POSITION, and branch on the element's KIND (#806).
				// Both were assumptions inherited rather than measured, and `--report item-effect-kinds` measured
				// them: of the 24 kinds the 4,753 decryptable item templates carry, exactly ONE reaches this table -
				// 53045 `Modify`, flat - and the positional and key-addressed readers agree on all 6,167 rows, 0
				// differences. So neither change moves a value today. They are here because the SAFETY was three
				// accidents deep and none of them is a rule:
				//   - `AddLayeredSkinnedMesh` (5,632 elements, the second-commonest kind) has no stat slot at all,
				//     and the positional reader was filing its key-194 MESH id as a stat id - harmless only because
				//     no mesh id survives `ApplicableStatIds`;
				//   - `SpellLineStatModifier` puts the LINE first and the stat second, and was rejected only because
				//     a line id (44221) is above the parser's stat-id bound;
				//   - `InterpolatedModify` is a level RAMP whose stored value is the ramp's LOW endpoint, and this
				//     table has no ramp columns - the one item carrying it (Ring of Acheron) touches a stat the
				//     whitelist drops.
				// Widening `ApplicableStatIds` by one id is all it takes to turn any of those into a wrong stat, so
				// the kind is TESTED and anything else is COUNTED and named rather than silently summed.
				var mods = new Dictionary<uint, int>();
				foreach (var m in ItemTemplateStatModifiers.ParseSpellStatEffects(template.AsSpan(12)))
				{
					if (!ApplicableStatIds.Contains(m.StatId))
						continue;

					if (m.Kind != FlatModifierKind)
					{
						// Not a flat additive delta, so `MaxStatAggregator` cannot sum it: a percent kind would add
						// +200 for "+2%", a ramp's low endpoint under-grants everyone above its low level, and a
						// `SetFlag`/`Set` is not an addition at all. Skipped and reported - the day the catalog grows
						// one of these on a stat we apply, the import says so instead of shipping a wrong number.
						nonFlatKinds[m.Kind] = nonFlatKinds.GetValueOrDefault(m.Kind) + 1;
						continue;
					}

					mods[m.StatId] = (int)m.WireAmount; // region is authoritative; last (== only) write wins
				}

				if (SyncStatModifiers(item, mods))
					statsAdded++;

				// The item's ON-USE SPELL (#762): a `CastNano` GameAction element - kind 53051, key 39 - whose payload
				// is a Spells.Id. This is the scalable twin of CaptureItemUseImporter, which can only learn an on-use
				// spell from a capture where somebody actually right-clicked the item.
				//
				// ⚠️ Only fills a GAP; a captured value always wins. The capture observed the spell the server really
				// applied, while the template states what the item declares - and those disagree at least once
				// (Totem of Origins: template 3763119 "Totem of Origins" vs captured 3981764 "Degeneration"), which is
				// unresolved. Overwriting a captured value with the template's would silently rewrite ground truth.
				foreach (var element in ItemTemplateStatModifiers.ParseElements(template.AsSpan(12)))
				{
					if (element.Kind != CastNanoKind || item.OnUseSpell != null)
						continue;

					if (element.Value(CastNanoSpellKey) is not { } spellId || spellId == 0)
						continue;

					if (!knownSpells.Contains(spellId))
					{
						onUseUnknownSpell++;
						continue;
					}

					item.OnUseSpell = spellId;
					onUseAdded++;
				}

				// The consumable's SHARED COOLDOWN (#764), from the same element pass. Both keys or neither: a line with
				// no duration cannot lock anything, and a duration with no line has nothing to lock.
				foreach (var element in ItemTemplateStatModifiers.ParseElements(template.AsSpan(12)))
				{
					if (element.Kind != LockSpellLineKind)
						continue;

					if (element.Value(LockSpellLineKey) is not { } line || line == 0)
						continue;
					if (element.Value(LockSpellLineDurationKey) is not { } seconds || seconds == 0)
						continue;

					if (item.OnUseLockSpellLine == line && item.OnUseLockSeconds == seconds)
						continue;

					item.OnUseLockSpellLine = line;
					item.OnUseLockSeconds = seconds;
					lockLinesAdded++;
				}

				// Weapon MIN/MAX damage (docs/rdb/item-template-stats.md, #395): property fields 286 (0x11E) = min and
				// 285 (0x11D) = max, decoded from the client's GUIGetMinDamage()/GUIGetMaxDamage(). Same strict property
				// run as value/quality. Stored only when both are present and sane (min <= max, non-zero) - non-weapons
				// (and weapons whose fields sit in the non-monotonic tail) leave them NULL.
				var hasMin = TryReadPropertyField(template.AsSpan(12), 286, out var minDmg);
				var hasMax = TryReadPropertyField(template.AsSpan(12), 285, out var maxDmg);
				if (hasMin && hasMax && minDmg > 0 && minDmg <= maxDmg)
				{
					if (item.MinDamage != (int)minDmg || item.MaxDamage != (int)maxDmg)
					{
						item.MinDamage = (int)minDmg;
						item.MaxDamage = (int)maxDmg;
						weaponDamageAdded++;
					}
				}

				// ATTACK RANGE (#838): property field 287, the client's own `Attack Range` stat, in world units x100.
				// It is what tells an NPC whether to close or to hold: every captured NPC melee weapon reads 200 and
				// every bow 1750. The client tuning table has no AI stand-off constant and a mob's swing names no
				// target to measure a distance from, so the weapon IS the source for it.
				if (TryReadPropertyField(template.AsSpan(12), 287, out var attackRange) && attackRange > 0 && item.AttackRange != (int)attackRange)
				{
					item.AttackRange = (int)attackRange;
					attackRangeAdded++;
				}

				// BAG capacity: property field 104 (= the Bag container kind 0x68) holds the extra backpack slots a bag
				// adds when equipped (the "+N slots" tooltip) - Burlap 24 / Bag of Holding 20 / Veteran's Bag 35. It sits
				// in the table's non-monotonic tail (after the strictly-decreasing run), so it needs the dedicated scan.
				// Only real bags carry it; used server-side only for the item equipped in the bag slot (equip slot 3).
				if (TryReadBagSlots(template.AsSpan(12), BagPropertyField, out var bagSlots) && item.BagExtraSlots != bagSlots)
				{
					item.BagExtraSlots = bagSlots;
					bagsAdded++;
				}

				// RESOURCE-bag capacity: property field 1331 (the resource-bag analogue of field 104, same non-monotonic
				// tail). Only real resource bags carry it; used server-side only for the item in the resource-bag slot
				// (equip slot 26). Max Resource Bag = 102.
				if (TryReadBagSlots(template.AsSpan(12), ResourceBagPropertyField, out var resourceSlots) && item.ResourceBagExtraSlots != resourceSlots)
				{
					item.ResourceBagExtraSlots = resourceSlots;
					bagsAdded++;
				}

				// Fill the display name only when we don't already have one (captured names are authoritative). The two
				// strings are read together because they are ONE decode - the description is simply the entry after
				// the name - but they are stored independently, since an item can arrive from a capture with a name
				// and no text.
				var (templateName, templateDescription) = ExtractText(template);
				if (string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrEmpty(templateName))
				{
					item.Name = templateName;
					namesAdded++;
				}

				// #891: the tooltip text. Most items have none - a plain armour piece carries its name and stops -
				// so this fills the ones that DO and leaves the rest null rather than inventing prose. Captured text
				// wins for the same reason the name does.
				if (string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrEmpty(templateDescription))
				{
					item.Description = templateDescription;
					descriptionsAdded++;
				}

				if (dumpDir != null)
					await File.WriteAllBytesAsync(Path.Combine(dumpDir, $"{matchedType}_{item.Id}.bin"), template);

				if (!firstSampleShown)
				{
					Console.WriteLine($"  sample: id={item.Id} \"{item.Name}\" type={matchedType} templateBytes={template.Length}");
					Console.WriteLine($"          payload[12..]= {Convert.ToHexString(template.AsSpan(12, Math.Min(48, template.Length - 12)))}");
					firstSampleShown = true;
				}
			}

			// The *Bonus columns were set on tracked Item entities above, so they save with everything else. (The old
			// ItemStat-row replace is gone: at most one primary attribute per item and SetAttributeBonus clears the
			// other four, so a changed/moved modifier can't leave a stale value.)
			// ⚠️ Ask the CHANGE TRACKER, not a hand-maintained list of counters. This was an OR over every counter the
			// importer happened to keep, so a field added later saved only when some OTHER field also changed on the
			// same run: `AttackRange` (#838) set 632 items, reported 632, and wrote nothing, because on a re-run of an
			// already-imported catalog every other counter is 0. The count is not the fact - the save is.
			if (db.ChangeTracker.HasChanges())
				await db.SaveChangesAsync();

			if (bagsAdded > 0)
				Console.WriteLine($"  bag capacities (field 104) set on {bagsAdded} item(s).");

			if (gemSlotsAdded > 0)
				Console.WriteLine($"  NumGemSlots (field 114) set on {gemSlotsAdded} item(s); {items.Count(i => i.GemSlots > 0)} are socketable (#754).");

			if (flagsAdded > 0 || bindFieldsAdded > 0)
			{
				// ⚠️ `BindsOnPickup` derives through `Flags is { } f && ...`, NOT `(i.Flags & Bit) != 0`: `Flags` is
				// `uint?` and a LIFTED `!= 0` is TRUE for null, which counted all 61 items with no readable template
				// as bound the first time this was written.
				Console.WriteLine($"  Flags (field 0) set on {flagsAdded} item(s), MoreFlags/Can (177/30) on {bindFieldsAdded}; " +
					$"{items.Count(i => i.BindsOnPickup)} bind on PICKUP, {items.Count(i => i.BindsOnEquip)} on EQUIP (#756).");
			}

			if (weaponDamageAdded > 0)
				Console.WriteLine($"  weapon min/max damage (fields 286/285) set on {weaponDamageAdded} item(s).");
			if (attackRangeAdded > 0)
				Console.WriteLine($"  weapon attack range (field 287) set on {attackRangeAdded} item(s).");

			if (lockLinesAdded > 0)
				Console.WriteLine($"  {lockLinesAdded} consumable(s) got their SHARED-cooldown line from CoreLockSpellLineTemplate (#764).");

			if (nonFlatKinds.Count > 0)
				Console.WriteLine($"  ⚠ {nonFlatKinds.Values.Sum()} stat element(s) on an APPLIED stat carry a non-flat kind and were skipped (#806): "
					+ string.Join(", ", nonFlatKinds.OrderByDescending(k => k.Value)
						.Select(k => $"{k.Key} {GameActionTypes.Get(k.Key)?.Name ?? "<unregistered>"} x{k.Value}"))
					+ " - ItemStatModifier has no kind column, so these need one before they can be stored.");

			if (onUseAdded > 0 || onUseUnknownSpell > 0)
				Console.WriteLine($"  on-use spells (CastNano, kind 53051) set on {onUseAdded} item(s); " +
					$"{onUseUnknownSpell} named a spell the catalog does not hold and were skipped (#762).");

			return new Result(items.Count, decrypted, noEntry, headerMismatch, decryptFailed, namesAdded, descriptionsAdded, categoriesAdded, valuesAdded, qualitiesAdded, statsAdded);
		}

		private static bool IsPrintableAscii(ReadOnlySpan<byte> s)
		{
			foreach (var c in s)
			{
				if (c is < 32 or > 126)
					return false;
			}

			return true;
		}

		// Reconciles the item's StatModifiers collection to exactly `desired` (wire stat id -> raw wire amount): adds new
		// rows, updates changed amounts, removes rows no longer present (a modifier that moved/vanished leaves nothing
		// stale). Keyed on (ItemId, StatId), so this is a clean per-(item,stat) upsert on re-import. Returns whether
		// anything changed (drives the counter + the save).
		private static bool SyncStatModifiers(Item item, IReadOnlyDictionary<uint, int> desired)
		{
			var changed = false;

			foreach (var existing in item.StatModifiers.ToList())
			{
				if (!desired.ContainsKey((uint)existing.StatId))
				{
					item.StatModifiers.Remove(existing);
					changed = true;
				}
			}

			foreach (var (statId, amount) in desired)
			{
				var row = item.StatModifiers.FirstOrDefault(m => (uint)m.StatId == statId);
				if (row == null)
				{
					item.StatModifiers.Add(new ItemStatModifier
					{
						ItemId = item.Id,
						StatId = (CharacterStatId)statId,
						Amount = amount
					});
					changed = true;
				}
				else if (row.Amount != amount)
				{
					row.Amount = amount;
					changed = true;
				}
			}

			return changed;
		}

		private static bool TryReadBagSlots(ReadOnlySpan<byte> body, uint fieldId, out uint slots)
		{
			slots = 0;
			for (var i = 0; i + 8 <= body.Length; i += 4)
			{
				if (body.ReadU32Le(i) != fieldId)
					continue;

				var v = body.ReadU32Le(i + 4);
				if (v is >= 1 and <= 512)
				{
					slots = v;
					return true;
				}
			}

			return false;
		}

		// Reads the value of property-table field `fieldId` from a decrypted template body (the span AFTER the
		// 12-byte RDB-Id header). The property table is the longest run of [fieldId u32][value u32] LE pairs with
		// strictly-decreasing fieldId in (0, 2000] - the same grammar tools/capture/decode_item_template.py validated.
		// Returns false when no such run contains the field (some template kinds don't carry it).
		//
		// `internal` because the item-stat-scaling report needs the SAME parse. ReportCli used to carry a
		// byte-identical private copy called TryReadTemplateField, justified as "kept local so the report stays
		// read-only and independent of the importer's internals" - but this is a pure parse over a span, so sharing
		// it neither writes anything nor exposes importer state. Two copies of a grammar this fiddly (a
		// strictly-decreasing run, not a fixed offset) is how a report quietly stops agreeing with the importer.
		internal static bool TryReadPropertyField(ReadOnlySpan<byte> body, uint fieldId, out uint value)
		{
			value = 0;
			var words = body.Length / 4;
			var bestLen = 0;
			var bestValue = 0u;
			var bestFound = false;
			for (var start = 0; start + 1 < words; start++)
			{
				uint last = 0;
				var haveLast = false;
				var len = 0;
				var found = false;
				var fv = 0u;
				for (var j = start; j + 1 < words; j++)
				{
					var fid = body.ReadU32Le(j * 4);
					if (fid == 0 || fid > 2000 || (haveLast && fid >= last))
						break;

					var val = body.ReadU32Le((j + 1) * 4);
					if (fid == fieldId)
					{
						found = true;
						fv = val;
					}

					last = fid;
					haveLast = true;
					len++;
					j++; // consumed the value word too
				}

				if (len > bestLen)
				{
					bestLen = len;
					bestFound = found;
					bestValue = fv;
				}
			}

			if (bestFound)
				value = bestValue;
			return bestFound;
		}

		// Direct scan for field 499 (item quality, 0-indexed) at any 4-byte-aligned position, taking the following word
		// as the value and accepting only the 0..5 field range. Mirrors TryReadBagSlots: the fallback for templates
		// whose 499 is outside the strict-decreasing property run (#397).
		private static bool TryReadQualityAnywhere(ReadOnlySpan<byte> body, out uint value)
		{
			value = 0;
			for (var i = 0; i + 8 <= body.Length; i += 4)
			{
				if (body.ReadU32Le(i) != 499)
					continue;

				var v = body.ReadU32Le(i + 4);
				if (v <= MaxQualityFieldValue)
				{
					value = v;
					return true;
				}
			}

			return false;
		}

		// Template strings backslash-escape apostrophes/quotes (e.g. "Aspirant\'s Cap"); undo that for the stored name.
		private static string Unescape(string s)
		{
			return s.Contains('\\')
				? s.Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\")
				: s;
		}

		public readonly record struct Result(
			int Keyed,
			int Decrypted,
			int NoEntry,
			int HeaderMismatch,
			int DecryptFailed,
			int NamesAdded,
			int DescriptionsAdded,
			int CategoriesAdded,
			int ValuesAdded,
			int QualitiesAdded,
			int StatsAdded);
	}
}
