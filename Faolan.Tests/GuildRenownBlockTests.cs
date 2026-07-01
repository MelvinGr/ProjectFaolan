using Faolan.CSPlayerAgent;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gates for the typed 277-byte guild renown block codec (Gitea #219). The grammar was recovered
	// from Ghidra (CoreGuild_c::Read, govForm 1) and validated against both 0x203F snapshots in the guild_invite
	// capture; SpawnGuildRenownBlock.bin is the captured "Sepulchra Invictus" block. The round-trip + the
	// structured CapturedPlaceholder both reproduce it, which is how the from-DB path replaces the old verbatim
	// blob (which had been hand-transcribed 3 bytes short -> 274 not 277, misaligning the roster after it).
	public class GuildRenownBlockTests
	{
		// The structured factory (no captured blob) reproduces the capture byte-for-byte - so GuildService can
		// emit a constructed block instead of carrying the verbatim (and previously malformed) template.
		[Fact]
		public void CapturedPlaceholder_EncodesToTheCapturedBlock()
		{
			Assert.Equal(Golden.Bytes("GuildRenownBlock"), GuildRenownBlock.CapturedPlaceholder().Encode());
		}

		[Fact]
		public void Decode_RecoversTheKnownScaffold()
		{
			var block = GuildRenownBlock.Decode(Golden.Bytes("GuildRenownBlock"));

			Assert.Equal((byte)0x01, block.Flag);
			Assert.Equal(GuildRenownBlock.StatSlotIds, block.StatIds);
			Assert.Equal(3, block.List2.Length); // the second 0x3F1 list (count 3)
			// RenownCounters[1] is the renown total that ticked between the two captured snapshots.
			Assert.Equal(20085677ul, block.RenownCounters[1]);
		}

		[Fact]
		public void Decode_ReEncode_IsByteIdentical()
		{
			var golden = Golden.Bytes("GuildRenownBlock");

			var block = GuildRenownBlock.Decode(golden);
			Assert.Equal(golden, block.Encode());
		}

		[Fact]
		public void ForFreshGuild_ZeroesRenownButStaysByteAligned()
		{
			var fresh = GuildRenownBlock.ForFreshGuild();

			Assert.All(fresh.RenownCounters, c => Assert.Equal(0ul, c));
			Assert.All(fresh.Counters64C, c => Assert.Equal(0ul, c));
			Assert.Equal(GuildRenownBlock.StatSlotIds, fresh.StatIds); // scaffold preserved
			Assert.Equal(GuildRenownBlock.Length, fresh.Encode().Length); // still 277 bytes -> roster stays aligned
		}

		// The live path builds the block from Guild.Renown: the total lands in RenownCounters[1] (the proven slot),
		// everything else stays the zeroed fresh-guild scaffold, and the block stays 277 bytes so the roster aligns.
		[Fact]
		public void ForRenown_SplicesTheTotalIntoTheProvenSlotAndStaysAligned()
		{
			var block = GuildRenownBlock.ForRenown(54321);

			Assert.Equal(54321ul, block.RenownCounters[1]);
			Assert.Equal(0ul, block.RenownCounters[0]); // only the total slot is set
			Assert.All(block.Counters64C, c => Assert.Equal(0ul, c));
			Assert.Equal(GuildRenownBlock.Length, block.Encode().Length);
		}

		// A guild with no accumulated renown is byte-identical to the fresh-guild scaffold.
		[Fact]
		public void ForRenown_ZeroEqualsFreshGuild()
		{
			Assert.Equal(GuildRenownBlock.ForFreshGuild().Encode(), GuildRenownBlock.ForRenown(0).Encode());
		}
	}
}
