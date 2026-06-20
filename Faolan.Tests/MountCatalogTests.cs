using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The summonable-mount catalog (#27): the capture-grounded mount ids resolve to their creature + summon spell.
	public class MountCatalogTests
	{
		[Fact]
		public void FadingBlackHorse_ResolvesByIdAndName()
		{
			var byId = MountCatalog.ForId(0x3E34F1u);
			Assert.NotNull(byId);
			Assert.Equal("Fading Black Horse", byId!.CreatureName);
			Assert.Equal(0x3BAD7Du, byId.SummonSpellId);

			Assert.Same(byId, MountCatalog.ForName("fading black horse"));
			Assert.Equal(byId, MountCatalog.Default);
		}

		[Fact]
		public void UnknownId_ResolvesNull()
		{
			Assert.Null(MountCatalog.ForId(0xDEADBEEFu));
		}
	}
}
