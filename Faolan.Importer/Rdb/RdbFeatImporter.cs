using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// Builds the FEAT catalog (Gitea #251) from the client's RDB, with no key crack and no capture.
	//
	// RDB type 1000020 holds items AND feats. An item template is TEA-encrypted (`RDBH`); a feat is PLAINTEXT and
	// opens with the "TB" magic, so ~20.8% of the 98,048 records read without a key - and that share is essentially
	// the feats, because the items are the encrypted part. `--report feat-catalog` prints the split.
	//
	// Record layout:
	//   [u32 magic 0x000F4254][u32 id][u32 1000000][...u32 fields...][u32 nameLen][name ASCII]
	// The name length is a u32, NOT a byte - and word [16] is a block count (2 for the 96-byte form, 3/4 for the
	// longer ones), so the name is found by an aligned scan rather than at a fixed offset.
	public static class RdbFeatImporter
	{
		// Word [44] on a feat record. The item templates that survive in plaintext carry 0x200; every other value in
		// the catalog is a sub-650-row rump of TEST/debug entries.
		public const uint FeatFlags = 0x10000005;

		// One effect element: 8 fixed descriptor words then (StatId, Value).
		private const int EffectElementLength = 40;
		private const uint ListUnit = 0x3F1;

		private const uint PlaintextMagic = 0x000F4254;
		private const uint TemplateType = 1000020;

		// The client's own feat TABLE: FeatTable_t::LoadStaticData reads this resource and its entries carry the
		// wire-id -> template-id pair (#467).
		private const uint FeatTableId = 1;
		private const uint FeatTableType = 1000042;
		private const int MapEntryLength = 48;

		// Columns 2/3/4 of an entry: up to three prerequisite wire feat ids.
		private const int PrereqSlots = 3;
		private const int MapHeaderLength = 20;

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? proxyBase = null, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			// Read the client's feat TABLE first: it gates the sweep below. A template the table POINTS AT is a feat by
			// definition, which is a far better test than the Flags heuristic - Hakotep's Hellfire (3187268) carries
			// Flags 0x5, not 0x10000005, and was silently excluded until the id map was used as the authority.
			var (map, prereqs) = ReadIdMapAndPrereqs(rdb);
			var mappedTemplates = map.Select(m => m.Template).ToHashSet();

			var found = new List<Feat>();
			var featEffects = new Dictionary<uint, List<(uint StatId, uint Value)>>();
			var skippedUnnamed = 0;

			foreach (var e in rdb.EntriesOfType(TemplateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // a record we cannot read is not a feat we can silently drop a column of - it is skipped whole
				}

				// Either the client's feat table points at it, or it carries the feat Flags value. The union matters:
				// the table covers 6,593 templates (every feat the WIRE can name) while Flags reaches a wider catalog
				// the table does not enumerate.
				if (body.Length < 48 || Word(body, 0) != PlaintextMagic || (Word(body, 44) != FeatFlags && !mappedTemplates.Contains(e.Id)))
					continue;

				var (name, description) = ReadNameAndDescription(body);
				if (name.Length == 0)
				{
					skippedUnnamed++;
					continue;
				}

				var parsed = ParseName(name);
				var effects = ParseEffects(body);
				featEffects[e.Id] = effects;
				found.Add(new Feat
				{
					Id = e.Id,
					Name = name,
					Flags = Word(body, 44),
					TemplateCategory = Word(body, 28),
					Tree = parsed.Tree,
					Group = parsed.Group,
					BaseName = parsed.BaseName,
					Rank = parsed.Rank,
					Description = description
				});
			}

			// The wire-id -> template-id map (#467). Delete+reinsert: it is wholly derived from one RDB resource.
			var mapRows = 0;
			var prereqRows = 0;
			if (map.Count > 0)
			{
				// TWO saves, deliberately. FeatPrereq cascades off FeatIdMap, so deleting the old map rows and adding
				// the new ones in ONE change-tracker run makes EF cascade the delete onto the prereq rows we just
				// added - they vanish on save, silently, and the table ends up empty.
				db.FeatPrereqs.RemoveRange(await db.FeatPrereqs.ToListAsync());
				db.FeatIdMaps.RemoveRange(await db.FeatIdMaps.ToListAsync());
				await db.SaveChangesAsync();
				foreach (var (wire, template, baseIndex, rootFlag) in map)
				{
					db.FeatIdMaps.Add(new FeatIdMap
					{
						WireFeatId = wire,
						TemplateId = template,
						BaseIndex = baseIndex,
						RootFlag = rootFlag
					});
					mapRows++;
				}

				// Only edges whose OWNER is a mapped feat: the FK points at FeatIdMap, and an orphan edge would fail
				// the insert rather than being quietly useful.
				var mapped = map.Select(m => m.Wire).ToHashSet();
				foreach (var (wire, sequence, requires) in prereqs)
				{
					if (!mapped.Contains(wire))
						continue;

					db.FeatPrereqs.Add(new FeatPrereq
					{
						WireFeatId = wire,
						Sequence = sequence,
						RequiresWireFeatId = requires
					});
					prereqRows++;
				}
			}

			var existing = await db.Feats.ToDictionaryAsync(f => f.Id);
			var added = 0;
			var updated = 0;
			foreach (var feat in found)
			{
				if (!existing.TryGetValue(feat.Id, out var row))
				{
					db.Feats.Add(feat);
					added++;
					continue;
				}

				if (row.Name == feat.Name && row.Description == feat.Description && row.Tree == feat.Tree && row.Group == feat.Group && row.BaseName == feat.BaseName
					&& row.Rank == feat.Rank && row.Flags == feat.Flags && row.TemplateCategory == feat.TemplateCategory)
					continue;

				row.Name = feat.Name;
				row.Description = feat.Description;
				row.Tree = feat.Tree;
				row.Group = feat.Group;
				row.BaseName = feat.BaseName;
				row.Rank = feat.Rank;
				row.Flags = feat.Flags;
				row.TemplateCategory = feat.TemplateCategory;
				updated++;
			}

			// Effects are delete+reinsert per feat: the list is wholly derived from the record, so reconciling element
			// by element would only risk leaving a stale tail behind.
			var effectRows = 0;
			var wanted = featEffects.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
			var stale = await db.FeatEffects.Where(fe => wanted.Keys.Contains(fe.FeatId)).ToListAsync();
			db.FeatEffects.RemoveRange(stale);
			foreach (var (featId, list) in wanted)
			{
				for (var i = 0; i < list.Count; i++)
				{
					db.FeatEffects.Add(new FeatEffect
					{
						FeatId = featId,
						Sequence = i,
						StatId = (CharacterStatId)list[i].StatId,
						Value = list[i].Value
					});
					effectRows++;
				}
			}

			if (added > 0 || updated > 0 || effectRows > 0 || stale.Count > 0 || mapRows > 0 || prereqRows > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("RdbFeatImporter: {Found} feat templates ({Unnamed} unnamed, skipped); {Added} added, {Updated} updated, {Effects} effects, {Map} id-map rows, {Prereq} prereq edges.",
				found.Count, skippedUnnamed, added, updated, effectRows, mapRows, prereqRows);

			return new Result(found.Count, added, updated, skippedUnnamed, effectRows, mapRows, prereqRows);
		}

		// Splits a catalog name into its tree / group / base / rank. The client encodes all four in the string:
		//   "Soldier: Optional Training: Thrown Weapons 5" -> (Soldier, Optional Training, Thrown Weapons, 5)
		//   "Core Training: Poison Magic Rating 6"         -> (Core Training, null, Poison Magic Rating, 6)
		//   "Guardian: Optional Training: 2HE (3)"         -> (Guardian, Optional Training, 2HE, 3)
		//   "Light Blade Training 1"                       -> (null, null, Light Blade Training, 1)
		// A name that fits none of it keeps BaseName = the whole name and Rank = 0, so nothing is lost to a parse that
		// did not apply.
		public static Parsed ParseName(string name)
		{
			var parts = name.Split(':', StringSplitOptions.TrimEntries).Where(p => p.Length > 0).ToArray();
			var tail = parts.Length > 0 ? parts[^1] : name.Trim();

			string? tree = null;
			string? group = null;
			if (parts.Length >= 2)
				tree = parts[0];
			if (parts.Length >= 3)
				group = parts[1];

			// The rank is a trailing integer, bare ("... 5") or parenthesised ("... (3)").
			var rank = 0;
			var m = RankSuffix.Match(tail);
			if (m.Success)
			{
				rank = int.Parse(m.Groups["n"].Value);
				tail = tail[..m.Index].TrimEnd();
			}

			return new Parsed(tree, group, tail.Length > 0 ? tail : name.Trim(), rank);
		}

		private static readonly Regex RankSuffix = new(@"\s+(\((?<n>\d{1,3})\)|(?<n>\d{1,3}))$", RegexOptions.Compiled);

		// The record's NAME and DESCRIPTION. TWO shapes exist, and knowing only the first is what left 3,633 plaintext
		// records looking nameless - including every feat the three captured characters hold:
		//
		//   u32 form:  [u32 len][name]                                - a name alone
		//   u16 form:  [u16 nameLen][u16 descLen][name][description]
		//
		// Template 3187268 (the Hellfire hakotep unlocks on tape) is the u16 form: `08 00 4B 00 "Hellfire" "Reduces
		// the casting time of Hellfire Breath, but increases the re-use time."`. Both are scanned and whichever
		// VALIDATES wins; the u16 form is tried first because a length PAIR landing two printable runs back to back is
		// a far stronger signature than one u32 that merely looks plausible.
		//
		// Returns ("", "") when neither fits, so an unreadable record is skipped rather than guessed at.
		public static (string Name, string Description) ReadNameAndDescription(byte[] body)
		{
			for (var i = 8; i + 4 < body.Length; i++)
			{
				var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i));
				var descLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i + 2));
				if (nameLen is < 2 or > 96 || descLen is < 1 or > 1024 || i + 4 + nameLen + descLen > body.Length)
					continue;

				if (!IsPrintable(body, i + 4, nameLen) || !IsPrintable(body, i + 4 + nameLen, descLen))
					continue;

				return (Encoding.ASCII.GetString(body, i + 4, nameLen), Encoding.ASCII.GetString(body, i + 4 + nameLen, descLen));
			}

			for (var i = 8; i + 4 < body.Length; i += 4)
			{
				var len = Word(body, i);
				if (len is < 2 or > 96 || i + 4 + len > body.Length || !IsPrintable(body, i + 4, (int)len))
					continue;

				return (Encoding.ASCII.GetString(body, i + 4, (int)len), "");
			}

			return ("", "");
		}

		private static bool IsPrintable(byte[] body, int start, int length)
		{
			for (var i = start; i < start + length; i++)
			{
				if (body[i] is < 0x20 or > 0x7E)
					return false;
			}

			return true;
		}

		// Reads the wire-id -> template-id map out of RDB 1000042 id 1 - the resource FeatTable_t::LoadStaticData
		// loads. Header [u32 type][u32 id][u32 1][u32 count][u32 version], then `count` 48-byte entries whose first
		// two words are (wireId, templateId) and whose 11th is the base index.
		public static (List<(uint Wire, uint Template, uint BaseIndex, uint RootFlag)> Map, List<(uint Wire, int Sequence, uint Requires)> Prereqs)
			ReadIdMapAndPrereqs(RdbReader rdb)
		{
			var map = new List<(uint, uint, uint, uint)>();
			var prereqs = new List<(uint, int, uint)>();
			var matches = rdb.EntriesOfType(FeatTableType).Where(e => e.Id == FeatTableId).ToList();
			if (matches.Count == 0)
				return (map, prereqs);

			var entry = matches[0];
			byte[] body;
			try
			{
				body = rdb.ReadDecompressed(entry);
			}
			catch
			{
				return (map, prereqs);
			}

			return (ParseIdMap(body), ParsePrereqs(body));
		}

		// Split out from the RDB read so it can be tested against bytes without the client install.
		public static List<(uint Wire, uint Template, uint BaseIndex, uint RootFlag)> ParseIdMap(byte[] body)
		{
			var map = new List<(uint, uint, uint, uint)>();
			if (body.Length < MapHeaderLength || Word(body, 0) != FeatTableType)
				return map;

			var count = (int)Word(body, 12);
			if (count <= 0 || MapHeaderLength + ((long)count * MapEntryLength) > body.Length)
				return map;

			for (var i = 0; i < count; i++)
			{
				var at = MapHeaderLength + (i * MapEntryLength);
				var wire = Word(body, at);
				var template = Word(body, at + 4);
				if (wire == 0 || template == 0)
					continue; // a blank slot, not a mapping - carrying it would put a 0 -> 0 row in the table

				map.Add((wire, template, Word(body, at + 40), Word(body, at + 32)));
			}

			return map;
		}

		// The PREREQUISITE edges of the same entries: columns 2/3/4 (offsets +8/+12/+16) are wire feat ids a feat
		// depends on, zero where unused. Returned in column order so the rows keep the client's ordering.
		public static List<(uint Wire, int Sequence, uint Requires)> ParsePrereqs(byte[] body)
		{
			var edges = new List<(uint, int, uint)>();
			if (body.Length < MapHeaderLength || Word(body, 0) != FeatTableType)
				return edges;

			var count = (int)Word(body, 12);
			if (count <= 0 || MapHeaderLength + ((long)count * MapEntryLength) > body.Length)
				return edges;

			for (var i = 0; i < count; i++)
			{
				var at = MapHeaderLength + (i * MapEntryLength);
				var wire = Word(body, at);
				if (wire == 0)
					continue;

				for (var slot = 0; slot < PrereqSlots; slot++)
				{
					var requires = Word(body, at + 8 + (slot * 4));
					if (requires != 0)
						edges.Add((wire, slot, requires));
				}
			}

			return edges;
		}

		// The record's TAIL, after the name: [u32 2][u32 14][u32 ListFrame(n)] then n x 40-byte effect elements whose
		// last two words are (StatId, Value). The eight leading words of an element are a fixed descriptor.
		// Returns an empty list when the tail is not this shape - some records carry no effect block at all.
		public static List<(uint StatId, uint Value)> ParseEffects(byte[] body)
		{
			var effects = new List<(uint StatId, uint Value)>();
			var tail = TailStart(body);
			if (tail + 12 > body.Length)
				return effects;

			var frame = Word(body, tail + 8);
			if (frame == 0 || frame % ListUnit != 0)
				return effects;

			var count = (int)(frame / ListUnit) - 1;
			var first = tail + 12;
			if (count <= 0 || first + (count * EffectElementLength) > body.Length)
				return effects;

			for (var i = 0; i < count; i++)
			{
				var at = first + (i * EffectElementLength);
				effects.Add((Word(body, at + 32), Word(body, at + 36)));
			}

			return effects;
		}

		// Where the tail begins: just past the u32-length-prefixed name.
		private static int TailStart(byte[] body)
		{
			for (var i = 8; i + 4 < body.Length; i += 4)
			{
				var len = Word(body, i);
				if (len is < 2 or > 96 || i + 4 + len > body.Length)
					continue;

				var printable = true;
				foreach (var b in body.AsSpan(i + 4, (int)len))
				{
					if (b is < 0x20 or > 0x7E)
					{
						printable = false;
						break;
					}
				}

				if (printable)
					return i + 4 + (int)len;
			}

			return body.Length;
		}

		private static uint Word(byte[] body, int offset)
		{
			return offset + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)) : 0;
		}

		public readonly record struct Parsed(string? Tree, string? Group, string BaseName, int Rank);

		public readonly record struct Result(int Found, int Added, int Updated, int Unnamed, int Effects, int MapRows, int PrereqEdges);
	}
}
