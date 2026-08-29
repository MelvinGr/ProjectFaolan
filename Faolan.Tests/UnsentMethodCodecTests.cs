using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Pvp;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// The captured-but-never-sent methods from #874, each pinned against the body the corpus actually carries.
	// `--report method-bodies --report-arg <name>` is the oracle for every one of them.
	//
	// Several carry ONE frame, so there is no variance to carve by; what pins those is the client's own signature
	// adding up to the captured length exactly. That is stated per case rather than assumed.
	public class UnsentMethodCodecTests
	{
		// ⭐ The 106-byte `DispatchTeamMemberUpdate` body is `TeamMemberEventCodec`'s, exactly - the same
		// [teamClass][teamId][TeamMemberRecord] the join / leave / new-leader trio uses. A byte-exact round-trip is
		// what turns that from a guess into the reason the method joins their HandledMethods.
		private static readonly byte[] CapturedTeamMemberUpdate = Convert.FromHexString(
			"0000DEA9000F08DB0000C3500802E5D400064E6563723830000000290000005000000444000000030000000000000006000000000000016200"
			+ "00C79C000004440000DEA9000F08DB000000000000000100009C500050B38401000181813BC035EC000000010000000000");

		[Fact]
		public void TeamMemberUpdate_IsTheSameBodyAsTheOtherThreeTeamVerbs()
		{
			var parsed = TeamMemberEventCodec.Parse(CapturedTeamMemberUpdate);

			Assert.Equal(0x000F08DBu, parsed.TeamId);
			Assert.Equal("Necr80", parsed.Member.Name); // the captured record's own bytes, pseudonymised above (#465)
			Assert.Equal(CapturedTeamMemberUpdate, TeamMemberEventCodec.Build(parsed));

			Assert.Contains(CSPlayerInterfaceMethods.DispatchTeamMemberUpdate, TeamMemberEventCodec.HandledMethods);
		}

		// [u32 nCharID][u16 len][ascii] - and nCharID is a BARE handle, with no 0xC350 class word. 4 + 2 + 10 = 16
		// accounts for the whole captured frame.
		[Fact]
		public void UpdateName_IsABareHandleAndAString()
		{
			var captured = Convert.FromHexString("013184AE000A53747269636B656E6E65");

			var parsed = UpdateNameCodec.Parse(captured);

			Assert.Equal(0x013184AEu, parsed.CharacterId);
			Assert.Equal("Strickenne", parsed.Name);
			Assert.Equal(captured, UpdateNameCodec.Build(parsed));
		}

		// All three captured frames are byte-identical and carry Funcom's item-shop deep link.
		[Fact]
		public void OpenSiteInBrowser_IsOneLengthPrefixedUrl()
		{
			var captured = Convert.FromHexString(
				"0034687474703A2F2F616F6373686F702E66756E636F6D2E636F6D2F73686F776974656D3F62756E646C653D717569636B7374617274");

			var parsed = OpenSiteInBrowserCodec.Parse(captured);

			Assert.Equal("http://aocshop.funcom.com/showitem?bundle=quickstart", parsed.SiteName);
			Assert.Equal(captured, OpenSiteInBrowserCodec.Build(parsed));
		}

		// Twelve bytes of zero. The frame offers no variance, so the widths come from the signature's two fields
		// summing to 12 - a u64 for money, as everywhere else on this wire.
		[Fact]
		public void CashRepository_IsAU64AndAFeedbackWord()
		{
			var captured = new byte[12];

			var parsed = CashRepositoryCodec.Parse(captured);

			Assert.Equal(0UL, parsed.NewCash);
			Assert.Equal(0u, parsed.Feedback);
			Assert.Equal(captured, CashRepositoryCodec.Build(parsed));
			Assert.Equal(captured, CashRepositoryCodec.Build(0UL));
		}

		// The corpus carries BOTH values of this one - 00 in a city session, 01 inside an instance - so it is a real
		// per-playfield flag and not a constant.
		[Theory]
		[InlineData(0x00, false)]
		[InlineData(0x01, true)]
		public void InDungeonPlayfield_IsABoolTheCorpusShowsBothWaysOf(byte encoded, bool inDungeon)
		{
			byte[] captured = [encoded];

			Assert.Equal(inDungeon, DungeonPlayfieldCodec.Parse(captured).InDungeon);
			Assert.Equal(captured, DungeonPlayfieldCodec.Build(inDungeon));
		}

		// 0x0C = 12. A byte cannot hold a day in minutes or seconds, which is what says the unit is hours.
		[Fact]
		public void RankingCountdown_IsOneByteOfHours()
		{
			byte[] captured = [0x0C];

			Assert.Equal((byte)12, RankingCountdownCodec.Parse(captured).HoursUntilCalculation);
			Assert.Equal(captured, RankingCountdownCodec.Build(12));
		}

		// ⚠️ Eight frames, all byte-identical zero. The round-trip is all this can gate; what the field SELECTS is
		// not observable from a corpus that only ever asks for 0.
		[Fact]
		public void RequestGetEffect_IsOneWordAndTheCorpusOnlyEverAsksForZero()
		{
			var captured = new byte[4];

			Assert.Equal(0u, RequestGetEffectCodec.Parse(captured).Effect);
			Assert.Equal(captured, RequestGetEffectCodec.Build(0));
		}
	}
}
