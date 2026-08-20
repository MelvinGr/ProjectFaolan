using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// One `PFAccessWindow_` playfield record out of the FullChar snapshot tails (see AccessWindowCatalog).
	//
	// ⭐ Re-carved to the CLIENT's grammar (#464): the table is `PlayfieldAccessHolder`'s ListFrame-framed list
	// (`CoreCharacter\PlayfieldAccessHolder.cpp`, reader `FUN_0077d220`/`FUN_0077db70`), and under that carve every
	// one of the old model's four `Unknown*` fields DISSOLVED:
	//   - `UnknownBeforeChildMode` (u16) + the name's "trailing NUL" + `ChildMode` were ONE big-endian u32 - the
	//     CHILD COUNT, whose high bytes are zero because no entry has more than 3 children;
	//   - `PfAccessWindowChild.Unknown` was the two u16 length words of a child's two EMPTY strings;
	//   - `PfAccessWindowChildDetail.DetailUnknown` + the 3-char "ShortCode" were one big-endian u32 window id;
	//   - the childMode 1/2/3 "variants", "detail modes" and both lookahead heuristics were artifacts of walking
	//     a plain `count × child` list through a shifted frame.
	// What remains genuinely unnamed is exactly ONE field: the head's `Unknown` u32 (zero on all 86 records).
	public sealed class PfAccessWindowEntry
	{
		public uint PlayfieldId { get; init; }

		// A BOOLEAN on the client (its reader normalises `!= 0`); meaning undecoded. 0 or 1 on all 86 records.
		public byte Flag { get; init; }

		// 2 or 3 across the catalog (3 on the Khitai/expansion playfields).
		public uint Kind { get; init; }

		// 0 in all 86 catalog records; a real record field (the client stores it), name unknown (Gitea #464).
		public uint Unknown { get; init; }

		public string InternalName { get; init; } = null!;

		public List<PfAccessWindowChild> Children { get; init; } = [];

		public override string ToString()
		{
			var children = Children.Count == 0
				? ""
				: $" | children: {string.Join(", ", Children.Select(c => c.ToString()))}";

			return $"{PlayfieldId} {InternalName} flag={Flag} kind={Kind}{children}";
		}
	}

	// One child element of a PfAccessWindowEntry: `[u8 flag][u32 id][str displayName][str parentName]` on the wire.
	// A flag-0 child carries a bare id and two empty strings; a flag-1 child names a window and its parent window.
	public sealed class PfAccessWindowChild
	{
		// A BOOLEAN on the client; 0 = id-only, 1 = named window, on every captured child.
		public byte Flag { get; init; }

		// The old model read this u32's bytes as `[u8 DetailUnknown][3-char ShortCode]` ("M2W"); it is one id
		// (5.03M-5.06M across the catalog).
		public uint Id { get; init; }

		public string DisplayName { get; init; } = "";
		public string ParentName { get; init; } = "";

		public override string ToString()
		{
			return DisplayName.Length == 0
				? $"[{Flag}] {Id}"
				: $"[{Flag}] {Id} {DisplayName} < {ParentName}";
		}
	}
}
