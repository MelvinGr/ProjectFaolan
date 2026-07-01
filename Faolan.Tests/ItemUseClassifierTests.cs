using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Inventory;
using Xunit;

namespace Faolan.Tests
{
	// Item-use classification (#206): an item is mapped to its on-use behavior by stable template id, not by name.
	public class ItemUseClassifierTests
	{
		[Fact]
		public void Null_IsNone()
		{
			Assert.Equal(ItemUseType.None, ItemUseClassifier.Classify(null));
		}

		[Fact]
		public void TotemOfOrigins_ClassifiesAsRecall()
		{
			Assert.Equal(ItemUseType.TotemRecall,
			ItemUseClassifier.Classify(new Item
			{
				Id = 3514741,
				Name = "Totem of Origins"
			}));
		}

		[Fact]
		public void UnknownItem_IsNone_RegardlessOfName()
		{
			// A different item that merely mentions "totem" must NOT be treated as a recall - identity is the id.
			Assert.Equal(ItemUseType.None,
			ItemUseClassifier.Classify(new Item
			{
				Id = 999,
				Name = "Decorative Totem"
			}));
		}
	}
}
