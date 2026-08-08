using System;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// `DispatchInstanceSelectionForCurrentPlayfield` (#309) - the "Select instance" window's contents.
	//
	// TWO retail replies, both 38 bytes, both inlined rather than carved into the fixture archive. The second (#738)
	// is what turns this from a single sample into a gate: it is a DIFFERENT map and a different instance token, so
	// the layout is now pinned by two independent observations rather than by one that could have had map-shaped
	// coincidences in it.
	//
	// Both are the "only the current instance" answer - the second is annotated by the maintainer as *"tried to
	// 'select instace', no others where available"*, which is exactly what a one-element list means, and it is the
	// answer a server hosting one copy of each playfield has to produce.
	// [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json frame 10726]
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_6.tshark.json frame 4059]
	public class InstanceSelectionCodecTests
	{
		private const string CapturedReply = "00000001620000c79c000004420000000000000000000000000000000100009c500050b38000";

		private const uint YmirsPass = 1090;
		private const uint CapturedToken = 0x0050B380;

		// map, instance token, the whole 38-byte reply.
		public static readonly TheoryData<uint, uint, string> CapturedReplies = new TheoryData<uint, uint, string>
		{
			{ YmirsPass, CapturedToken, CapturedReply },
			{ 2070, 0x0050CB24, "00000001620000c79c000008160000000000000000000000000000000100009c500050cb2400" } // Poitain
		};

		[Theory, MemberData(nameof(CapturedReplies))]
		public void TheCapturedReply_ParsesIntoOnePinnedInstanceOfTheCurrentMap(uint mapId, uint token, string hex)
		{
			var reply = InstanceSelectionCodec.Parse(Convert.FromHexString(hex));

			var instance = Assert.Single(reply.Instances);
			Assert.Equal(mapId, instance.MapId);
			Assert.Equal(token, instance.InstanceToken);

			// Pinned to a live copy, normal difficulty, and owned by nobody - both are shared playfields, so the
			// owner pair is (0, 0). If this ever reads as owned, the slot alignment has drifted.
			Assert.Equal(PlayfieldProxy.PinnedInstanceWord, instance.InstanceWord);
			Assert.Equal(PlayfieldProxy.PlayfieldClass, instance.ClassWord);
			Assert.False(instance.IsEpic);
			Assert.False(instance.IsInstanced);
			Assert.False(instance.IsTeamOwned);
		}

		[Theory, MemberData(nameof(CapturedReplies))]
		public void BuildingTheCurrentInstance_ReproducesTheCapturedBytesExactly(uint mapId, uint token, string hex)
		{
			var built = InstanceSelectionCodec.Build(InstanceSelections.Current(mapId, token));

			Assert.Equal(hex, Convert.ToHexStringLower(built));
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
