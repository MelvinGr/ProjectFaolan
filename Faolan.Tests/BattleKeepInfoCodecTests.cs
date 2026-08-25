using System;
using Faolan.Core.Network.Codecs.Pvp;
using Xunit;

namespace Faolan.Tests
{
	// 0x20AA DispatchBattleKeepInfoRequestReturnPhase3And4 - the per-battlekeep status reply (#147). Bodies below
	// are retail's, verbatim from NecroL80_mage-necromancer_login_gameplay_annotated_9, where four request/reply
	// pairs were captured and each reply was followed by a guild-name update ("Vendetta", "Hand of Stygia",
	// "Fleurs de Lys") - the corpus DOES carry battlekeep traffic, contrary to #147's older "zero messages".
	public class BattleKeepInfoCodecTests
	{
		// keep 4 (Vendetta, guild 0x7B726), keep 2 (Hand of Stygia, guild 0x7C6A6), keep 3 (guild 0x7DFCA).
		private const string Keep4 = "000007e2000000040000deb70007b72600000000000000006a88ae4000000002";
		private const string Keep2 = "000007e2000000020000deb70007c6a600000000000000006a89570000000002";
		private const string Keep3 = "000007e2000000030000deb70007dfca00000000000000006a88bc5000000002";

		[Theory]
		[InlineData(Keep4)]
		[InlineData(Keep2)]
		[InlineData(Keep3)]
		public void CapturedReply_RoundTripsByteExact(string hex)
		{
			var body = Convert.FromHexString(hex);
			var parsed = BattleKeepInfoCodec.Parse(body);

			Assert.Single(parsed.Keeps);
			Assert.Equal(body, BattleKeepInfoCodec.Build(parsed));
		}

		[Fact]
		public void CapturedReply_DecodesTheKeepStatus()
		{
			var keep = BattleKeepInfoCodec.Parse(Convert.FromHexString(Keep4)).Keeps[0];

			Assert.Equal(4u, keep.KeepId);
			Assert.Equal(0x7B726u, keep.OwningGuildId); // Vendetta - matches the MassivePvp board + the following 0x2041
			Assert.Equal(0u, keep.AttackingGuildClass); // the null ID32: no war declared on any captured keep
			Assert.Equal(0u, keep.AttackingGuildId);
			Assert.Equal(0x6A88AE40u, keep.NextPhaseUnix);
			Assert.Equal(BattleKeepPhase.SilenceBeforeTheStorm, keep.Phase);
		}

		[Fact]
		public void TheGuildIds_MatchTheOwningGuild()
		{
			// Cross-corroboration with the same session's captured guild names.
			Assert.Equal(0x7B726u, BattleKeepInfoCodec.Parse(Convert.FromHexString(Keep4)).Keeps[0].OwningGuildId);
			Assert.Equal(0x7C6A6u, BattleKeepInfoCodec.Parse(Convert.FromHexString(Keep2)).Keeps[0].OwningGuildId);
			Assert.Equal(0x7DFCAu, BattleKeepInfoCodec.Parse(Convert.FromHexString(Keep3)).Keeps[0].OwningGuildId);
		}

		[Fact]
		public void TheOwnerIsAGuildId32_SoAnotherClassWordIsRefused()
		{
			// The word after keepId is the owning guild's ID32 CLASS (EntityClass.Guild), which the client's keep
			// window reads as one ID32 with the id that follows - a reply carrying any other class there is not a
			// keep record, and Parse says so rather than carrying it as a "zone word".
			var body = Convert.FromHexString(Keep4.Replace("0000deb7", "0000c350"));
			Assert.Throws<Faolan.Core.Extensions.CodecParseException>(() => BattleKeepInfoCodec.Parse(body));
		}
	}
}
