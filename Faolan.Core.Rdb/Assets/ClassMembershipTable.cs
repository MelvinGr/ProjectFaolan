using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	// The client's CLASS MEMBERSHIP table (RDB type 1000506) — which classes each archetype holds.
	//
	// ⭐ `ConanArchetypes` had to take this from the game's published documentation, on the finding that "the client
	// ships the archetype IDS (1000505 tier 1) but no MEMBERSHIP table — not in the class registry, not in the race x
	// class table (1000507), and not in the CHARACTERCREATOR XML (1010700)". **1000506 is that table**, and its
	// loader says so in its own words: `FUN_0078de40` fails it with *"Could not load classmembership info. This will
	// cause gui to loose parent class feats"*. All twelve placements agree exactly, so the wiki citation is now a
	// corroboration rather than the source.
	//
	// Grammar (little-endian), a plain `DataTable_c` — the client's parse callback `FUN_0078e870` checks
	// `length >= count * 8` and appends each row's SECOND word to the vector belonging to the class named by its
	// first:
	//
	//   [u32 type][u32 id][u32 version][u32 count]
	//   count * [u32 parentClassId][u32 childClassId]
	//
	// **Two entries ship, and they are not versions of each other:**
	//   id 0 — 27 edges, the full AUTHORED hierarchy, including classes this build never shipped (Soldier alone has
	//          six children where three exist)
	//   id 1 — 16 edges, the SHIPPED set: `Commoner -> the four archetypes`, then three classes each.
	//
	// So id 1 is the one to check a runtime table against; id 0 is design history. Both parse identically.
	//
	// The class ids are 1000505's (`RDBPlayerClassData_c`, `[classId][name][tier]`), which is also where the tier-1
	// archetype ids come from — so a row's parent is a tier-1 id and its child a tier-3 one, except the root row
	// where `Commoner (0)` parents the archetypes themselves.
	public static class ClassMembershipTable
	{
		public const uint RdbType = 1000506;

		/// <summary>The entry carrying the SHIPPED hierarchy; entry 0 is the wider authored one.</summary>
		public const uint ShippedId = 1;

		private const int HeaderBytes = 16;
		private const int Stride = 8;

		public static ClassMembership Parse(byte[] body)
		{
			var declared = body.Length >= HeaderBytes ? body.ReadU32Le(12) : 0;
			var edges = new List<(uint Parent, uint Child)>();

			for (var offset = HeaderBytes; offset + Stride <= body.Length; offset += Stride)
				edges.Add((body.ReadU32Le(offset), body.ReadU32Le(offset + 4)));

			return new ClassMembership(edges, declared, body.Length - HeaderBytes - edges.Count * Stride);
		}
	}

	// `Edges` is (parent, child) in file order. `DeclaredCount` and `TrailingBytes` are reported rather than checked
	// so a caller can print the agreement — the same rule `CreatureRaceTable` follows.
	public sealed record ClassMembership(IReadOnlyList<(uint Parent, uint Child)> Edges, uint DeclaredCount, int TrailingBytes)
	{
		/// <summary>The children of one parent, in file order.</summary>
		public IEnumerable<uint> ChildrenOf(uint parent)
		{
			return Edges.Where(e => e.Parent == parent).Select(e => e.Child);
		}

		/// <summary>Every parent that has children, in file order.</summary>
		public IEnumerable<uint> Parents => Edges.Select(e => e.Parent).Distinct();
	}
}
