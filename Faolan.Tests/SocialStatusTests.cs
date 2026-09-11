using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Text;
using Xunit;

namespace Faolan.Tests
{
	// The chat tier's presence enum: c2s SetSocialStatus (0x002A) and the friend record's status byte, which
	// are the same enum in the two directions.
	//
	// The three c2s bodies below are every occurrence in the corpus - all in `NecroL80_..._annotated_9`, each ~50
	// microseconds after the matching `SetLookingForTeamStatus` on the CS tier, which is what identifies the values.
	public class SocialStatusTests
	{
		[Theory]
		[InlineData("00000002", SocialStatus.LookingForGroup)] // f1071, 50us after the lfg flag went on (f3809, on again seven minutes later, is byte-identical)
		[InlineData("00000001", SocialStatus.Online)] //          f1094, 59us after it went off
		public void CapturedBody_ParsesAndRebuilds(string hex, SocialStatus expected)
		{
			var body = Convert.FromHexString(hex);

			Assert.Equal(expected, SocialStatusCodec.Parse(body));
			Assert.Equal(body, SocialStatusCodec.Build(expected));
		}

		[Fact]
		public void Parse_IsStrictAboutTheWidth()
		{
			Assert.ThrowsAny<Exception>(() => SocialStatusCodec.Parse(Convert.FromHexString("000002")));
			Assert.ThrowsAny<Exception>(() => SocialStatusCodec.Parse(Convert.FromHexString("0000000200")));
		}

		// Deliberately not strict about the value: the client's own renderer treats anything outside 1..3 as offline
		// rather than rejecting the record, so an unknown status is a value we do not know and not a body we failed to
		// read. `Away` is in the enum and in no capture, which is exactly this case one step further along.
		[Fact]
		public void Parse_PassesAnUnknownStatusThrough()
		{
			Assert.Equal((SocialStatus)9, SocialStatusCodec.Parse(Convert.FromHexString("00000009")));
			Assert.Equal(SocialStatus.Away, SocialStatusCodec.Parse(Convert.FromHexString("00000003")));
		}

		// The friend record carries the same enum in one byte. These two are captured retail records (the theory in
		// TeamTradeFriendCodecTests gates all six against their bytes); what is asserted here is that the byte reads
		// back as a status rather than a flag.
		[Theory]
		[InlineData("01318405010701eda8b22b", SocialStatus.Online)]
		[InlineData("013184ae00056a64ad512c", SocialStatus.Offline)]
		public void FriendState_ReadsTheStatusByte(string hex, SocialStatus expected)
		{
			var state = FriendStateCodec.Parse(Convert.FromHexString(hex));

			Assert.Equal(expected, state.Status);
			Assert.Equal(expected != SocialStatus.Offline, state.Online);
		}

		// Widening the field must not have moved anything: the bool overload every existing caller uses still produces
		// the captured bytes, and the status overload differs from it in exactly one byte - the status one.
		[Fact]
		public void StatusOccupiesTheOnlineByteAndNothingElse()
		{
			var online = FriendStateCodec.Build(0x01318405u, true, 7, ConanClass.HeraldOfXotli, 0x01EDA8B2u);
			var lfg = FriendStateCodec.Build(0x01318405u, SocialStatus.LookingForGroup, 7, ConanClass.HeraldOfXotli, 0x01EDA8B2u);

			Assert.Equal("01318405010701eda8b22b", Convert.ToHexString(online).ToLowerInvariant()); // the captured record
			Assert.Equal(online.Length, lfg.Length);

			var differing = 0;
			for (var i = 0; i < online.Length; i++)
			{
				if (online[i] != lfg[i])
					differing++;
			}

			Assert.Equal(1, differing);
			Assert.Equal((byte)SocialStatus.LookingForGroup, lfg[4]);
		}

		// A friend with no live connection is Offline, and all three online statuses answer the stamp union the same
		// way - the union asks "is this a last-seen or a playfield token", which lfg and Away do not change.
		[Theory]
		[InlineData(SocialStatus.Online, true)]
		[InlineData(SocialStatus.LookingForGroup, true)]
		[InlineData(SocialStatus.Away, true)]
		[InlineData(SocialStatus.Offline, false)]
		public void OnlineIsEveryStatusButOffline(SocialStatus status, bool expected)
		{
			Assert.Equal(expected, new FriendState(1, status, 5, ConanClass.Barbarian, 0).Online);
		}
	}
}
