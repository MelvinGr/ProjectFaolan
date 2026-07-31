using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
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
		public void RideBuffPayload_ReproducesTheCapturedFrame520Blob()
		{
			// The verbatim 54-byte ride-buff payload from the mount capture (frame 520): duration@8 = 8_640_000 cs
			// (24 h), spell base 0x31EB15 @22, magnitude 80, effect nonce 0x1560F9BB @50. Now CONSTRUCTED via
			// MagicBuffDatas.Apply instead of a verbatim blob.
			byte[] captured =
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x83, 0xD6, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x31, 0xEB, 0x15, 0x00, 0x00, 0x00, 0x50,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00, 0x00, 0x15, 0x60, 0xF9, 0xBB
			];

			Assert.Equal(captured, MagicBuffDataCodec.Build(MountCatalog.RideBuffPayload(80))); // the mount capture is a level-80 character (#438)
		}

		[Fact]
		public void UnknownId_ResolvesNull()
		{
			Assert.Null(MountCatalog.ForId(0xDEADBEEFu));
		}
	}
}
