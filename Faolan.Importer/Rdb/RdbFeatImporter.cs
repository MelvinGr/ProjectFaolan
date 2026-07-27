using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
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

		private const uint PlaintextMagic = 0x000F4254;
		private const uint TemplateType = 1000020;

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? proxyBase = null, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);
			var found = new List<Feat>();
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

				if (body.Length < 48 || Word(body, 0) != PlaintextMagic || Word(body, 44) != FeatFlags)
					continue;

				var name = ReadName(body);
				if (name.Length == 0)
				{
					skippedUnnamed++;
					continue;
				}

				var parsed = ParseName(name);
				found.Add(new Feat
				{
					Id = e.Id,
					Name = name,
					Flags = Word(body, 44),
					TemplateCategory = Word(body, 28),
					Tree = parsed.Tree,
					Group = parsed.Group,
					BaseName = parsed.BaseName,
					Rank = parsed.Rank
				});
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

				if (row.Name == feat.Name && row.Tree == feat.Tree && row.Group == feat.Group && row.BaseName == feat.BaseName
					&& row.Rank == feat.Rank && row.Flags == feat.Flags && row.TemplateCategory == feat.TemplateCategory)
					continue;

				row.Name = feat.Name;
				row.Tree = feat.Tree;
				row.Group = feat.Group;
				row.BaseName = feat.BaseName;
				row.Rank = feat.Rank;
				row.Flags = feat.Flags;
				row.TemplateCategory = feat.TemplateCategory;
				updated++;
			}

			if (added > 0 || updated > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("RdbFeatImporter: {Found} feat templates ({Unnamed} unnamed, skipped); {Added} added, {Updated} updated.",
				found.Count, skippedUnnamed, added, updated);

			return new Result(found.Count, added, updated, skippedUnnamed);
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

		// The u32-length-prefixed ASCII name. Scanned u32-aligned because the header length varies with the record's
		// block count; returns "" when nothing parses as one, so an unnamed record is skipped rather than guessed at.
		private static string ReadName(byte[] body)
		{
			for (var i = 8; i + 4 < body.Length; i += 4)
			{
				var len = Word(body, i);
				if (len is < 2 or > 96 || i + 4 + len > body.Length)
					continue;

				var span = body.AsSpan(i + 4, (int)len);
				var printable = true;
				foreach (var b in span)
				{
					if (b is < 0x20 or > 0x7E)
					{
						printable = false;
						break;
					}
				}

				if (printable)
					return Encoding.ASCII.GetString(span);
			}

			return "";
		}

		private static uint Word(byte[] body, int offset)
		{
			return offset + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)) : 0;
		}

		public readonly record struct Parsed(string? Tree, string? Group, string BaseName, int Rank);

		public readonly record struct Result(int Found, int Added, int Updated, int Unnamed);
	}
}
