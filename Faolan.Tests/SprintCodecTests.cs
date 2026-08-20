using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// The sprint/regen family (#867). These bodies are one to five bytes, so what can be wrong about them is not a
	// blob comparison - it is the GRAMMAR: how many fields the body has, and which byte is which. That is exactly what
	// was wrong before, so the regression case is the point of the file.
	//
	// The values are the ones `--report sprint` measures over 134 captures; the report is the oracle, and it is
	// re-runnable, so these carry no retail bytes of their own (#555).
	public class SprintCodecTests
	{
		// ⚠️ THE REGRESSION. `SetSprintInputState(bool sprintInput, bool clientCheck)` is two bytes, and the corpus
		// carries all four combinations: 00 01 x3714, 00 00 x2954, 01 01 x1958, 01 00 x703. Read as one u16 and
		// compared to 1 - the pre-#867 reading - `00 01` is the ONLY match, which is the player NOT sprinting. So the
		// old code sprinted on the commonest not-sprinting frame and missed all 2,661 sprinting ones.
		[Theory]
		[InlineData(0x00, 0x01, false, true)]
		[InlineData(0x00, 0x00, false, false)]
		[InlineData(0x01, 0x01, true, true)]
		[InlineData(0x01, 0x00, true, false)]
		public void SprintInputState_IsTwoBools_NotOneU16(byte first, byte second, bool sprintInput, bool clientCheck)
		{
			byte[] body = [first, second];

			var parsed = SprintInputStateCodec.Parse(body);

			Assert.Equal(sprintInput, parsed.SprintInput);
			Assert.Equal(clientCheck, parsed.ClientCheck);
			Assert.Equal(body, SprintInputStateCodec.Build(parsed));

			// The reading this replaced: a u16 == 1 selects the frame where sprintInput is FALSE.
			var asSingleU16 = (first << 8) | second;
			Assert.Equal(asSingleU16 == 1, !sprintInput && clientCheck);
		}

		// `DispatchSetIsSprinting(isSprinting)` / `DispatchSyncCanSprint(canSprint)` /
		// `DispatchSyncRegenDelayed(regenDelayed)` - one byte each, and the same one byte, which is why they share a
		// codec. The corpus carries only 00 and 01 for all three.
		[Theory]
		[InlineData(0x00, false)]
		[InlineData(0x01, true)]
		public void SprintFlag_IsOneByte(byte encoded, bool on)
		{
			byte[] body = [encoded];

			Assert.Equal(on, SprintFlagCodec.Parse(body).On);
			Assert.Equal(body, SprintFlagCodec.Build(on));
		}

		[Fact]
		public void SprintFlag_CoversTheThreeMethodsThatShareItsBody()
		{
			Assert.Equal(
			[
				GameCharInterfaceProjectMethods.DispatchSetIsSprinting,
				GameCharInterfaceProjectMethods.DispatchSyncCanSprint,
				GameCharInterfaceProjectMethods.DispatchSyncRegenDelayed
			], SprintFlagCodec.HandledMethods);
		}

		// `DispatchUpdateSprintbar(energyValue, forceUpdate)` - the two modal bodies in the corpus are 00 00 23 28 01
		// and 00 00 36 B0 01, i.e. 9,000 and 14,000 with forceUpdate set. Those are class ENERGY maxima, which is what
		// says the field is an absolute value rather than a delta.
		[Theory]
		[InlineData(new byte[] { 0x00, 0x00, 0x23, 0x28, 0x01 }, 9000u, true)]
		[InlineData(new byte[] { 0x00, 0x00, 0x36, 0xB0, 0x01 }, 14000u, true)]
		public void SprintBar_IsABigEndianValueAndAForceFlag(byte[] body, uint energy, bool force)
		{
			var parsed = SprintBarCodec.Parse(body);

			Assert.Equal(energy, parsed.EnergyValue);
			Assert.Equal(force, parsed.ForceUpdate);
			Assert.Equal(body, SprintBarCodec.Build(parsed));
			Assert.Equal(body, SprintBarCodec.Build(energy));
		}
	}
}
