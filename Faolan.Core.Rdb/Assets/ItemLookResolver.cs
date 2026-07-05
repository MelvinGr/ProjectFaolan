using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Faolan.Core.Rdb.Assets
{
	// Resolves an equipped item to the worn armour MESHINDEXFILE(s) the client skins onto the character body (#275 gear).
	//
	// The worn mesh is NOT the item's look-id (template offset 16 - that is a dye/appearance grouping that happens to
	// collide with unrelated resource ids). It is carried by field-id 9 inside the item template's trailing binary
	// sub-record: a byte-packed `... 09 00 00 00 <meshIndexId u32> ...` entry whose value resolves to a MESHINDEXFILE
	// (type 1010030) under `items\armor`. An item can carry several field-9 entries (e.g. male + female variants of one
	// garment, or a garment plus a shared base body); the caller filters by the character's sex via the mesh path.
	//
	// This is authoritative (the client reads the same template) and fully offline - no login cache / Preview.bin / live
	// client is involved, so any character's real gear renders from faolan.db alone. See docs/gear-resolver-re-handoff.md.
	public static class ItemLookResolver
	{
		// The field-id that tags a worn-mesh reference in the template's trailing sub-record.
		private const uint WornMeshField = 9;

		// Extract every distinct worn armour meshindex tagged by field 9 in a DECRYPTED item template (the raw bytes from
		// RdbReader.ReadDecrypted, including the 12-byte RDB-Id header). Ordered by first appearance.
		public static IReadOnlyList<WornMesh> FromTemplate(RdbReader rdb, ReadOnlySpan<byte> decrypted)
		{
			var result = new List<WornMesh>();
			var seen = new HashSet<uint>();

			// Byte-packed sub-record, so scan every byte offset (not word-aligned): a field-9 tag followed by the value.
			for (var i = 4; i + 4 <= decrypted.Length; i++)
			{
				if (ReadU32(decrypted, i - 4) != WornMeshField)
					continue;

				var meshId = ReadU32(decrypted, i);
				if (meshId is < 1000 or > 20_000_000 || !seen.Add(meshId))
					continue;

				if (!rdb.TryGet(RdbAssetExtractor.MeshIndexFileType, meshId, out var entry) || entry.Size == 0)
					continue;

				MeshIndexFile mesh;
				try
				{
					mesh = MeshIndexFile.Parse(meshId, rdb.ReadDecompressed(entry));
				}
				catch
				{
					continue;
				}

				var src = mesh.SourceFile;
				if (src == null || !src.Replace('/', '\\').ToLowerInvariant().Contains(@"items\armor"))
					continue;

				result.Add(new WornMesh(meshId, src, mesh.Skinned));
			}

			return result;
		}

		public static IReadOnlyList<uint> SelectForGender(IReadOnlyList<WornMesh> meshes, string? wantGender)
		{
			if (wantGender == null)
				return meshes.Select(m => m.MeshIndexId).ToList();

			var picked = new List<uint>();
			foreach (var group in meshes.GroupBy(m => Garment(m.SourcePath)))
			{
				var list = group.ToList();
				var hasPair = list.Any(m => m.Gender == "Female") && list.Any(m => m.Gender == "Male");
				if (hasPair)
				{
					picked.Add(list.FirstOrDefault(m => m.Gender == wantGender).MeshIndexId is var id and not 0
						? id
						: list[0].MeshIndexId);
				}
				else
					picked.AddRange(list.Select(m => m.MeshIndexId));
			}

			return picked;
		}

		// The worn meshindex ids for one item, with same-garment male/female variants collapsed to the wanted gender.
		// An item can legitimately carry several DIFFERENT pieces (e.g. the slave manacles carry both the handcuff mesh
		// and a base cotton body) - those are kept; only a genuine M/F pair of the SAME garment is a variant choice.
		// wantGender is "Female"/"Male" (null => keep everything). Falls back to the opposite/neutral variant when the
		// wanted gender is absent, so an item that only ships one gender still renders.
		public static IReadOnlyList<uint> WornMeshIds(RdbReader rdb, ReadOnlySpan<byte> decrypted, string? wantGender)
		{
			return SelectForGender(FromTemplate(rdb, decrypted), wantGender);
		}

		// Normalise a mesh export path to a garment identity so male/female variants of one garment collapse together:
		// drop the directory, the .max, the set numbers, and the male/female + boilerplate tokens (set/global/mats).
		private static string Garment(string sourcePath)
		{
			var name = sourcePath.Replace('/', '\\');
			var slash = name.LastIndexOf('\\');
			if (slash >= 0)
				name = name[(slash + 1)..];
			name = name.ToLowerInvariant().Replace(".max", "");
			// Separators + digits to spaces FIRST so the \b gender/boilerplate strip works (underscore is a \w char, so
			// "\bmale\b" would never match inside "cloth_male").
			name = Regex.Replace(name, @"[\d_\-]+", " ");
			name = Regex.Replace(name, @"\b(fe)?male\b|\b(set|global|mats|conan)\b", " ");
			return Regex.Replace(name, @"\s+", " ").Trim();
		}

		private static uint ReadU32(ReadOnlySpan<byte> d, int i)
		{
			return (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24));
		}

		public readonly record struct WornMesh(uint MeshIndexId, string SourcePath, bool Skinned)
		{
			// The gender the mesh's export path names, or null when the asset is gender-neutral. "female" is tested
			// first because it contains "male" as a substring.
			public string? Gender
			{
				get
				{
					var p = SourcePath.Replace('/', '\\').ToLowerInvariant();
					if (p.Contains("female"))
						return "Female";
					if (p.Contains("male"))
						return "Male";

					return null;
				}
			}
		}
	}
}
