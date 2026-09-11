using System;
using System.Buffers.Binary;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// RDB 1000506, the client's archetype -> class membership. The shipped table lives in the client install,
	// so this gates the reader on the exact shape it uses and pins the finding that matters: the hierarchy the client
	// states is the one `ConanArchetypes` hard-codes.
	//
	// `--report class-hierarchy` is the live check against the real RDB; this is the half that runs from a plain
	// clone, and the edges below are the shipped entry 1 verbatim.
	public class ClassMembershipTableTests
	{
		// RDB 1000506/1 - [type][id][version][count=16] then 16 x [parent][child], little-endian.
		private static byte[] ShippedEntry()
		{
			(uint Parent, uint Child)[] edges =
			[
				(0, 1), (0, 2), (0, 3), (0, 4),
				(1, 20), (1, 22), (1, 31),
				(2, 24), (2, 28), (2, 29),
				(3, 18), (3, 34), (3, 39),
				(4, 41), (4, 43), (4, 44)
			];

			var body = new byte[16 + edges.Length * 8];
			BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), ClassMembershipTable.RdbType);
			BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), ClassMembershipTable.ShippedId);
			BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)edges.Length);
			for (var i = 0; i < edges.Length; i++)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16 + i * 8), edges[i].Parent);
				BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20 + i * 8), edges[i].Child);
			}

			return body;
		}

		[Fact]
		public void Parse_ReadsEveryEdgeAndNothingIsLeftOver()
		{
			var table = ClassMembershipTable.Parse(ShippedEntry());

			Assert.Equal(16, table.Edges.Count);
			Assert.Equal(16u, table.DeclaredCount);
			Assert.Equal(0, table.TrailingBytes);
		}

		// The root row is what makes this a hierarchy rather than a flat archetype->class map: Commoner parents the
		// four archetypes, which is why `ConanArchetypes` has no Commoner entry.
		[Fact]
		public void CommonerParentsTheFourArchetypes()
		{
			var table = ClassMembershipTable.Parse(ShippedEntry());

			Assert.Equal(
				[(uint)ConanArchetype.Soldier, (uint)ConanArchetype.Priest, (uint)ConanArchetype.Rogue, (uint)ConanArchetype.Mage],
				table.ChildrenOf((uint)ConanArchetype.Commoner));
		}

		// The client's own table and the hard-coded constant agree on all twelve placements, so the table - not the
		// game's published documentation - is what the constant answers to.
		[Theory]
		[InlineData(ConanArchetype.Soldier)]
		[InlineData(ConanArchetype.Priest)]
		[InlineData(ConanArchetype.Rogue)]
		[InlineData(ConanArchetype.Mage)]
		public void EveryArchetypesClasses_MatchTheClientsTable(ConanArchetype archetype)
		{
			var table = ClassMembershipTable.Parse(ShippedEntry());

			Assert.Equal(
				ConanArchetypes.ClassesOf(archetype).Select(c => (uint)c).Order(),
				table.ChildrenOf((uint)archetype).Order());
		}
	}
}
