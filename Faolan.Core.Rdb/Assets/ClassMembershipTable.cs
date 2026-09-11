using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     The client's class membership table (RDB type 1000506): which classes each archetype holds. Its loader
	///     (<c>FUN_0078de40</c>) names it, failing with "Could not load classmembership info. This will cause gui to
	///     loose parent class feats"; all twelve placements agree with the game's published class hierarchy.
	/// </summary>
	/// <remarks>
	///     The record is a plain <c>DataTable_c</c>, little-endian. The client's parse callback
	///     (<c>FUN_0078e870</c>) checks <c>length &gt;= count * 8</c> and appends each row's second word to the vector
	///     belonging to the class named by its first:
	///     <code>
	///     [u32 type][u32 id][u32 version][u32 count]
	///     count * [u32 parentClassId][u32 childClassId]
	///     </code>
	///     <para>
	///         Two entries ship and they are not versions of each other. Id 0 holds 27 edges, the full authored
	///         hierarchy, including classes this build never shipped — Soldier alone has six children where three
	///         exist. Id 1 holds 16 edges, the shipped set: Commoner parenting the four archetypes, then three classes
	///         each. Id 1 is therefore the one to check a runtime table against. Both parse identically.
	///     </para>
	///     <para>
	///         The class ids are 1000505's (<c>RDBPlayerClassData_c</c>, <c>[classId][name][tier]</c>), which is also
	///         where the tier-1 archetype ids come from, so a row's parent is a tier-1 id and its child a tier-3 one,
	///         except the root row where Commoner (0) parents the archetypes themselves.
	///     </para>
	/// </remarks>
	public static class ClassMembershipTable
	{
		public const uint RdbType = 1000506;

		/// <summary>The entry carrying the shipped hierarchy; entry 0 is the wider authored one.</summary>
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

	/// <summary>
	///     The parsed table. <c>Edges</c> is (parent, child) in file order, and the declared count and trailing byte
	///     count are reported rather than checked so a caller can print the agreement, as
	///     <see cref="CreatureRaceTable" /> does.
	/// </summary>
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
