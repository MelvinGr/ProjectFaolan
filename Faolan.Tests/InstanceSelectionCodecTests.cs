using System;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// `DispatchInstanceSelectionForCurrentPlayfield` (#309) - the "Select instance" window's contents.
	//
	// The corpus contains exactly ONE of these, so it is inlined rather than carved into the fixture archive: 38
	// bytes, the reply retail sent when the player clicked Select Instance while standing in Ymir's Pass.
	// [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json frame 10726]
	public class InstanceSelectionCodecTests
	{
		private const string CapturedReply = "00000001620000c79c000004420000000000000000000000000000000100009c500050b38000";

		private const uint YmirsPass = 1090;
		private const uint CapturedToken = 0x0050B380;

		[Fact]
		public void TheCapturedReply_ParsesIntoOnePinnedInstanceOfTheCurrentMap()
		{
			var reply = InstanceSelectionCodec.Parse(Convert.FromHexString(CapturedReply));

			var instance = Assert.Single(reply.Instances);
			Assert.Equal(YmirsPass, instance.MapId);
			Assert.Equal(CapturedToken, instance.InstanceToken);

			// Pinned to a live copy, normal difficulty, and owned by nobody - Ymir's Pass is a shared playfield, so
			// the owner pair is (0, 0). If this ever reads as owned, the slot alignment has drifted.
			Assert.Equal(PlayfieldProxy.PinnedInstanceWord, instance.InstanceWord);
			Assert.Equal(PlayfieldProxy.PlayfieldClass, instance.ClassWord);
			Assert.False(instance.IsEpic);
			Assert.False(instance.IsInstanced);
			Assert.False(instance.IsTeamOwned);
		}

		[Fact]
		public void BuildingTheCurrentInstance_ReproducesTheCapturedBytesExactly()
		{
			var built = InstanceSelectionCodec.Build(InstanceSelections.Current(YmirsPass, CapturedToken));

			Assert.Equal(CapturedReply, Convert.ToHexStringLower(built));
		}

		// The count is a PLAIN u32, not the (n+1)*0x3F1 list frame. A one-element list is 0x00000001 here, where a
		// framed one would be 0x000007E2 - the trap this project keeps re-finding, so it is pinned rather than
		// left implicit in the golden above.
		[Fact]
		public void TheCountIsAPlainU32_NotAListFrame()
		{
			var built = InstanceSelectionCodec.Build(InstanceSelections.Current(YmirsPass, CapturedToken));

			Assert.Equal(1u, BitConverter.ToUInt32([built[3], built[2], built[1], built[0]], 0));
		}

		[Fact]
		public void AnEmptyList_RoundTrips()
		{
			var built = InstanceSelectionCodec.Build(InstanceSelections.None());

			Assert.Equal("00000000", Convert.ToHexStringLower(built));
			Assert.Empty(InstanceSelectionCodec.Parse(built).Instances);
		}

		[Fact]
		public void ParseIsStrict_ATruncatedOrOverlongBodyThrows()
		{
			var built = InstanceSelectionCodec.Build(InstanceSelections.Current(YmirsPass, CapturedToken));

			Assert.ThrowsAny<Exception>(() => InstanceSelectionCodec.Parse(built.AsSpan(0, built.Length - 1)));
			Assert.ThrowsAny<Exception>(() => InstanceSelectionCodec.Parse([.. built, 0x00]));
		}
	}
}
