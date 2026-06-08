using Faolan.GameServer.Spawn;
using Xunit;

namespace Faolan.Tests
{
	public class SpawnHandleTableTests
	{
		[Fact]
		public void Allocate_GivesUniqueSequentialHandles()
		{
			var table = new SpawnHandleTable();

			var a = table.Allocate();
			var b = table.Allocate();
			var c = table.Allocate();

			Assert.Equal(SpawnHandleTable.HandleBase, a);
			Assert.Equal(SpawnHandleTable.HandleBase + 1, b);
			Assert.Equal(SpawnHandleTable.HandleBase + 2, c);
		}

		[Fact]
		public void GetOrAllocate_IsStablePerKey()
		{
			var table = new SpawnHandleTable();

			var casilda = table.GetOrAllocate("casilda");
			var crocodile = table.GetOrAllocate("crocodile");

			Assert.Equal(casilda, table.GetOrAllocate("casilda"));
			Assert.NotEqual(casilda, crocodile);
			Assert.True(table.Contains("casilda"));
			Assert.False(table.Contains("unknown"));
		}

		[Fact]
		public void HandleBase_IsClearOfCharacterInstanceRange()
		{
			// Character instances are >= 0x02000000; allocated handles must not collide with them.
			Assert.True(SpawnHandleTable.HandleBase < 0x02000000u);
		}
	}
}
