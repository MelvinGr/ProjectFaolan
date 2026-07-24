using Faolan.Core;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gates for the typed 277-byte guild renown block codec (Gitea #219). The grammar was recovered
	// from Ghidra (CoreGuild_c::Read, govForm 1) and validated against both 0x203F snapshots in the guild_invite
	// capture; Golden/GuildRenownBlock.bin is the captured "Sepulchra Invictus" block. The round-trip + the
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
		public void Decode_ReEncode_IsByteIdentical()
		{
			var golden = Golden.Bytes("GuildRenownBlock");

			var block = GuildRenownBlock.Decode(golden);
			Assert.Equal(golden, block.Encode());

			// ICodec stream Read/ToArray round-trips too.
			Assert.Equal(golden, GuildRenownBlock.Read(new FaolanStream(golden)).ToArray());
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
		public void ForFreshGuild_MatchesTheFreshCreateBlockInFullUpdateForm()
		{
			var fresh = GuildRenownBlock.ForFreshGuild();
			var create = GuildRenownBlock.ForGuildCreate();

			// #335: a fresh guild's full-update IS its create block re-expressed in full-update FORM - same guild
			// STATE, so every guild-state field matches ForGuildCreate (byte-exact vs the real fresh-guild create
			// capture), including the two the old code wrongly carried as "hard caps".
			Assert.Equal(create.List2, fresh.List2);
			Assert.Equal(create.Group4, fresh.Group4); // [0, 0] - NOT the Sepulchra placeholder's [0, 30,000,000]
			Assert.Equal(create.Counters64D, fresh.Counters64D); // 0x01A93458 - NOT Sepulchra's 975,000,000
			Assert.Equal(create.Counters64C, fresh.Counters64C);
			Assert.All(fresh.RenownCounters, c => Assert.Equal(0ul, c));
			Assert.All(fresh.Group1, v => Assert.Equal(0u, v));
			Assert.All(fresh.Group3, v => Assert.Equal(0u, v));
			Assert.All(fresh.Counters64B, v => Assert.Equal(0ul, v));

			// ...and NONE of the Sepulchra placeholder's accumulated per-guild values leak in.
			Assert.NotEqual(30000000u, fresh.Group4[1]);
			Assert.NotEqual(975000000ul, fresh.Counters64D);

			// ...while the two FORM fields switch to the full-update shape (create writes Flag 0 + an empty stat list).
			Assert.Equal((byte)0x01, fresh.Flag);
			Assert.Equal(GuildRenownBlock.StatSlotIds, fresh.StatIds);
			Assert.Equal(GuildRenownBlock.Length, fresh.Encode().Length); // still 277 bytes -> roster stays aligned
		}

		// The create-time block (0x2044 tail) is the same codec, Flag=0 + empty StatIds -> 225 bytes. This reproduces
		// the create-guild capture's tail byte-for-byte, so GuildService.WriteGuildCreatedBody constructs it via the
		// typed codec instead of the old verbatim hex blob (#306).
		[Fact]
		public void ForGuildCreate_EncodesToTheCapturedCreateTail()
		{
			var block = GuildRenownBlock.ForGuildCreate();

			Assert.Equal(Golden.Bytes("GuildCreateRenownBlock"), block.Encode());
			Assert.Equal(225, block.Encode().Length);
			Assert.Equal((byte)0x00, block.Flag);
			Assert.Empty(block.StatIds);
		}

		// The live path builds the block from Guild.Renown: the total lands in RenownCounters[1] (the proven slot),
		// everything else stays the fresh-guild scaffold (== ForFreshGuild), and the block stays 277 bytes so the
		// roster aligns.
		[Fact]
		public void ForRenown_SplicesTheTotalIntoTheProvenSlotAndStaysAligned()
		{
			var block = GuildRenownBlock.ForRenown(54321);

			Assert.Equal(54321ul, block.RenownCounters[1]);
			Assert.Equal(0ul, block.RenownCounters[0]); // only the total slot is set
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
