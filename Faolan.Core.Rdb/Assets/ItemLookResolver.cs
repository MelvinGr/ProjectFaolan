using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Resolves an equipped item to the worn armour MESHINDEXFILEs the client skins onto the character body. See
	///     docs/rdb/gear-rendering.md.
	/// </summary>
	/// <remarks>
	///     The worn mesh is not the item's look-id (template offset 16 is a dye/appearance grouping that happens to
	///     collide with unrelated resource ids). It is carried by field-id 9 inside the item template's trailing binary
	///     sub-record: a byte-packed <c>... 09 00 00 00 &lt;meshIndexId u32&gt; ...</c> entry whose value resolves to a
	///     MESHINDEXFILE (type 1010030) under <c>items\armor</c>. An item can carry several field-9 entries — male and
	///     female variants of one garment, or a garment plus a shared base body — so the caller filters by the
	///     character's sex via the mesh path.
	///     <para>
	///         The client reads the same template, so this is authoritative, and it is fully offline: no login cache,
	///         Preview.bin or live client is involved, and any character's real gear renders from faolan.db alone.
	///     </para>
	/// </remarks>
	public static class ItemLookResolver
	{
		/// <summary>
		///     The field-id that tags a worn-mesh reference in the template's trailing sub-record.
		/// </summary>
		private const uint WornMeshField = 9;

		/// <summary>
		///     Extracts every distinct worn armour meshindex tagged by field 9 in a decrypted item template, ordered by
		///     first appearance.
		/// </summary>
		/// <param name="decrypted">
		///     The raw bytes from <see cref="RdbReader.ReadDecrypted" />, including the 12-byte RDB-Id header.
		/// </param>
		public static IReadOnlyList<WornMesh> FromTemplate(RdbReader rdb, ReadOnlySpan<byte> decrypted)
		{
			var result = new List<WornMesh>();
			var seen = new HashSet<uint>();

			// The sub-record is byte-packed rather than word-aligned, so every byte offset has to be probed for a
			// field-9 tag followed by its value.
			for (var i = 4; i + 4 <= decrypted.Length; i++)
			{
				if (decrypted.ReadU32Le(i - 4) != WornMeshField)
					continue;

				var meshId = decrypted.ReadU32Le(i);
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

		/// <summary>
		///     The worn meshindex ids for one item, with same-garment male/female variants collapsed to the wanted
		///     gender.
		/// </summary>
		/// <param name="wantGender">"Female" or "Male"; null keeps every mesh.</param>
		/// <remarks>
		///     An item can legitimately carry several different pieces — the slave manacles carry both the handcuff
		///     mesh and a base cotton body — and those are all kept; only a genuine male/female pair of the same
		///     garment is a variant choice. When the wanted gender is absent the opposite or neutral variant is used,
		///     so an item that only ships one gender still renders.
		/// </remarks>
		public static IReadOnlyList<uint> WornMeshIds(RdbReader rdb, ReadOnlySpan<byte> decrypted, string? wantGender)
		{
			return SelectForGender(FromTemplate(rdb, decrypted), wantGender);
		}

		/// <summary>
		///     Normalises a mesh export path to a garment identity so male/female variants of one garment collapse
		///     together: the directory, the .max, the set numbers and the male/female and boilerplate tokens
		///     (set/global/mats) are all dropped.
		/// </summary>
		private static string Garment(string sourcePath)
		{
			var name = sourcePath.Replace('/', '\\');
			var slash = name.LastIndexOf('\\');
			if (slash >= 0)
				name = name[(slash + 1)..];
			name = name.ToLowerInvariant().Replace(".max", "");
			// Separators and digits must become spaces before the \b gender/boilerplate strip runs: underscore is a
			// \w character, so "\bmale\b" would never match inside "cloth_male".
			name = Regex.Replace(name, @"[\d_\-]+", " ");
			name = Regex.Replace(name, @"\b(fe)?male\b|\b(set|global|mats|conan)\b", " ");
			return Regex.Replace(name, @"\s+", " ").Trim();
		}


		public readonly record struct WornMesh(uint MeshIndexId, string SourcePath, bool Skinned)
		{
			/// <summary>
			///     The gender the mesh's export path names, or null when the asset is gender-neutral. "female" is
			///     tested first because it contains "male" as a substring.
			/// </summary>
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
