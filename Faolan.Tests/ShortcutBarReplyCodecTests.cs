using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact golden for the ShortcutBarReply (0x2076) body decoded in #71: the action bar restored at login.
	// Reproduces Strie's captured login reply from her stored shortcuts (slot/type/id), proving the builder matches
	// retail - header = (N+1)*0x3F1, records = [slot][type][id][0][0][0].
	public class ShortcutBarReplyCodecTests
	{
		// Strie_rogue-assassin_login.tshark.json, the 0x2076 DispatchShortcutBarReply body (14 shortcuts).
		// header 0x00003b1f = (14+1)*0x3F1; then 14 records of [slot][type][id][0][0][0] (one per line, 48 hex each).
		private const string Zeros = "000000000000000000000000"; // types 2/3/4 leave the record tail zero (24 hex)

		private const uint StrieId = 0x02165EFF;

		// A type-1 record points into the owner's OWN inventory, and its tail says so: [0][0x68 = container 104][owner].
		// It is not padding. Reading it as padding is what made the strict codec throw on DemoL80's and NecroL80's bars and
		// abort the whole capture import (#530 wave 6 swapped a tolerant reader that discarded these three words).
		// Bytes from DemoL80_mage-demonologist_login_randomActions frame 98, record 40.
		private const uint DemoL80Id = 0x110099BD;

		private static readonly string StrieBody = "00003b1f" + "0000000000000004000bbc46" + Zeros + "0000000100000004000bbc4b" + Zeros + "0000000200000004000bbc47" + Zeros +
		                                           "0000000a00000004000bbc48" + Zeros + "0000000c00000004000bbc49" + Zeros + "0000006400000004000bbc46" + Zeros + "0000006500000004000bbc4b" + Zeros +
		                                           "0000006600000004000bbc47" + Zeros + "0000006e00000004000bbc48" + Zeros + "0000007000000004000bbc49" + Zeros + "00004e2000000004003815c0" + Zeros +
		                                           "00004e210000000400329b83" + Zeros + "00004e22000000040039e783" + Zeros + "00004e2300000004004b3ec8" + Zeros;

		[Fact]
		public void BuildBody_EmptyBar_HasOnlyThe0x3F1Header()
		{
			var body = ShortcutBarReplyCodec.Build(StrieId, []);
			Assert.Equal("000003f1", Convert.ToHexString(body).ToLowerInvariant()); // (0+1)*0x3F1
		}

		[Fact]
		public void BuildBody_ReproducesStriesCapturedBar_ByteExact()
		{
			var shortcuts = new[]
			{
				S(0, 4, 769094), S(1, 4, 769099), S(2, 4, 769095), S(10, 4, 769096), S(12, 4, 769097), S(100, 4, 769094), S(101, 4, 769099), S(102, 4, 769095), S(110, 4, 769096),
				S(112, 4, 769097), S(20000, 4, 3675584), S(20001, 4, 3316611), S(20002, 4, 3794819), S(20003, 4, 4931272)
			};

			var body = ShortcutBarReplyCodec.Build(StrieId, shortcuts);

			Assert.Equal(StrieBody, Convert.ToHexString(body).ToLowerInvariant());

			// Round-trip: Parse recovers the per-slot records and Build rebuilds them byte-for-byte.
			Assert.Equal(body, ShortcutBarReplyCodec.Build(ShortcutBarReplyCodec.Parse(body)));
		}

		[Fact]
		public void InventorySlotRecord_CarriesTheContainerAndOwner_NotPadding()
		{
			var body = ShortcutBarReplyCodec.Build(DemoL80Id, [S(0x4E40, 1, 0)]);

			// [count][slot][type=1][id][0][0x68][owner] - the captured shape, not three zeros.
			Assert.Equal("000007e2" + "00004e40" + "00000001" + "00000000" + "00000000" + "00000068" + "110099bd", Convert.ToHexString(body).ToLowerInvariant());

			var parsed = ShortcutBarReplyCodec.Parse(body);
			Assert.Equal(DemoL80Id, parsed.Owner); // the owner is recovered from the tail, not stored per row
			Assert.Equal(body, ShortcutBarReplyCodec.Build(parsed.Owner, [S(0x4E40, 1, 0)]));
		}

		private static CharacterShortcut S(uint slot, uint type, uint id)
		{
			return new CharacterShortcut
			{
				Slot = slot,
				ShortcutType = type,
				TargetId = id
			};
		}
	}
}
