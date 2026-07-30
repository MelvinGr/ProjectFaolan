using System;
using Faolan.Core;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Who a playfield instance BELONGS to, and how hard it is (#511/#512) - both of which turn out to live in the
	// PlayfieldProxy and had been read as something else.
	//
	// Goldens are real captured proxies from NecroL80_mage-necromancer_login_gameplay_chat-annotated-4 and the castle
	// captures. The proxy is `[u8 'b'][u32 ×8][u8 bool]`.
	public class PlayfieldInstanceOwnerTests
	{
		private static PlayfieldProxy Parse(string hex)
		{
			return PlayfieldProxy.Read(new FaolanStream(Convert.FromHexString(hex)));
		}

		// The group-instance entry: IntendedTeleport at 20:22:12 into the Amphitheatre of Karutonia (map 1092 =
		// 0x444). Slots [2]+[3] carry 0xDEA9 / 0x000F08DB - the team/guild class and the exact team id
		// DispatchYouJoinedTeam had announced three seconds earlier, and that the `~Team` channel subscribed to.
		//
		// This is #512's whole question answered on the wire: the instance is keyed to the TEAM, not the character.
		[Fact]
		public void GroupInstanceEntry_IsOwnedByTheTeam()
		{
			var proxy = Parse("620000c79c000004440000dea9000f08db0000000000000000000000000000000001");

			Assert.Equal(1092u, proxy.MapId);
			Assert.Equal(PlayfieldProxy.TeamOwnerType, proxy.OwnerType);
			Assert.Equal(0x000F08DBu, proxy.OwnerId);
			Assert.True(proxy.IsInstanced);
			Assert.True(proxy.IsTeamOwned);
			// The mode is orthogonal to ownership: this group instance is a NORMAL one.
			Assert.False(proxy.IsEpic);
		}

		// A PERSONAL instance - King Conan's castle (map 2040 = 0x7F8). Owner type 2 with a real owner id, which is
		// what used to read as "a per-map word" plus "a character handle that is 0 except on the revive body".
		[Fact]
		public void PersonalInstance_IsOwnedButNotByATeam()
		{
			var proxy = Parse("620000c79c000007f80000000272559bc7000000000000000100009c50005089ed00");

			Assert.Equal(2040u, proxy.MapId);
			Assert.Equal(PlayfieldProxy.PersonalOwnerType, proxy.OwnerType);
			Assert.Equal(0x72559BC7u, proxy.OwnerId);
			Assert.True(proxy.IsInstanced);
			Assert.False(proxy.IsTeamOwned);
		}

		// An ordinary SHARED playfield owns nothing - the overwhelmingly common case, and the baseline the two above
		// are meaningful against.
		[Fact]
		public void SharedPlayfield_HasNoOwner()
		{
			var proxy = Parse("620000c79c000003ec0000000000000000000000000000000100009c500050b36b00");

			Assert.Equal(1004u, proxy.MapId);
			Assert.Equal(0u, proxy.OwnerType);
			Assert.Equal(0u, proxy.OwnerId);
			Assert.False(proxy.IsInstanced);
			Assert.False(proxy.IsTeamOwned);
		}

		// #511: the difficulty MODE is the class word, and this is the controlled A/B that shows it - the same map
		// (1004, the Spider Caverns), entered twice 34 s apart, annotated `energed normal instance` then
		// `enered epic instance`. Everything but the class word and the instance token matches.
		[Fact]
		public void NormalAndEpic_DifferOnlyInTheClassWord()
		{
			var normal = Parse("620000c79c000003ec0000000000000000000000000000000100009c500050b36b00");
			var epic = Parse("620000cabc000003ec0000000000000000000000000000000200009c500050b36c00");

			Assert.Equal(normal.MapId, epic.MapId); // SAME map - the mode is not in the map id
			Assert.Equal(normal.OwnerType, epic.OwnerType);
			Assert.Equal(normal.OwnerId, epic.OwnerId);

			Assert.False(normal.IsEpic);
			Assert.True(epic.IsEpic);
			Assert.Equal(PlayfieldProxy.PlayfieldClass, normal.ClassWord);
			Assert.Equal(PlayfieldProxy.EpicPlayfieldClass, epic.ClassWord);
		}

		// Round-trip: what we build from the named fields must reproduce the captured bytes exactly, or the rename
		// above would be a comment change rather than a decode.
		[Theory]
		[InlineData("620000c79c000004440000dea9000f08db0000000000000000000000000000000001")]
		[InlineData("620000c79c000003ec0000000000000000000000000000000100009c500050b36b00")]
		[InlineData("620000cabc000003ec0000000000000000000000000000000200009c500050b36c00")]
		[InlineData("620000c79c000007f80000000272559bc7000000000000000100009c50005089ed00")]
		public void Proxy_RoundTrips(string hex)
		{
			var bytes = Convert.FromHexString(hex);
			var s = new FaolanStream();
			Parse(hex).Write(s);
			Assert.Equal(bytes, s.ToArray());
		}
	}
}
