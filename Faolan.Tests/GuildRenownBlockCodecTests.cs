using System;
using System.Security.Cryptography;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gates for the typed 277-byte guild renown block codec. The grammar was recovered
	// from Ghidra (CoreGuild_c::Read, govForm 1) and validated against both 0x203F snapshots in the guild_invite
	// capture; Golden/GuildRenownBlock.bin is the captured "Sepulchra Invictus" block. The round-trip and the
	// structured CapturedPlaceholder both reproduce it, which is what lets the from-DB path emit a constructed block.
	// The length is load-bearing: 274 bytes instead of 277 misaligns the roster that follows.
	public class GuildRenownBlockCodecTests
	{
		// The create-guild capture's 225-byte 0x2044 tail, byte-for-byte, as a SHA-256 pin: the retail blob itself is
		// not carried in the repo.
		private const string CapturedCreateTailSha256 = "890040917505eaf6418b843043290c06ac29e3ec794ffcb3ec9ced43377f4669";

		// The structured factory (no captured blob) reproduces the capture byte-for-byte - so GuildService can
		// emit a constructed block instead of carrying a verbatim template.
		[Fact]
		public void CapturedPlaceholder_EncodesToTheCapturedBlock()
		{
			Assert.Equal(Golden.Bytes("GuildRenownBlock"), GuildRenownBlockCodec.Build(GuildRenownBlock.CapturedPlaceholder()));
		}

		[Fact]
		public void Decode_ReEncode_IsByteIdentical()
		{
			var golden = Golden.Bytes("GuildRenownBlock");

			Assert.Equal(golden, GuildRenownBlockCodec.Build(GuildRenownBlockCodec.Parse(golden)));
		}

		[Fact]
		public void Decode_RecoversTheKnownScaffold()
		{
			var block = GuildRenownBlockCodec.Parse(Golden.Bytes("GuildRenownBlock"));

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

			// A fresh guild's full-update is its create block re-expressed in full-update form - same guild
			// state, so every guild-state field matches ForGuildCreate (byte-exact vs the real fresh-guild create
			// capture), including the two that are easily mistaken for hard caps.
			Assert.Equal(create.List2, fresh.List2);
			Assert.Equal(create.Group4, fresh.Group4); // [0, 0] - not the Sepulchra placeholder's [0, 30,000,000]
			Assert.Equal(create.Counters64D, fresh.Counters64D); // 0x01A93458 - not Sepulchra's 975,000,000
			Assert.Equal(create.Counters64C, fresh.Counters64C);
			Assert.All(fresh.RenownCounters, c => Assert.Equal(0ul, c));
			Assert.All(fresh.Group1, v => Assert.Equal(0u, v));
			Assert.All(fresh.Group3, v => Assert.Equal(0u, v));
			Assert.All(fresh.Counters64B, v => Assert.Equal(0ul, v));

			// ...and none of the Sepulchra placeholder's accumulated per-guild values leak in.
			Assert.NotEqual(30000000u, fresh.Group4[1]);
			Assert.NotEqual(975000000ul, fresh.Counters64D);

			// ...while the two form fields switch to the full-update shape (create writes Flag 0 + an empty stat list).
			Assert.Equal((byte)0x01, fresh.Flag);
			Assert.Equal(GuildRenownBlock.StatSlotIds, fresh.StatIds);
			Assert.Equal(GuildRenownBlockCodec.Length, GuildRenownBlockCodec.Build(fresh).Length); // still 277 bytes -> roster stays aligned
		}

		// The create-time block (0x2044 tail) is the same codec, Flag=0 + empty StatIds -> 225 bytes. This reproduces
		// the create-guild capture's tail byte-for-byte, so GuildService.WriteGuildCreatedBody constructs it via the
		// typed codec rather than a verbatim hex blob.
		[Fact]
		public void ForGuildCreate_EncodesToTheCapturedCreateTail()
		{
			var block = GuildRenownBlock.ForGuildCreate();

			// The retail tail's SHA-256 rather than the tail: the blob is gone from the repo. This gate only
			// ever compared against those bytes - it never read them - so the digest is the same assertion.
			Assert.Equal(225, GuildRenownBlockCodec.Build(block).Length);
			Assert.Equal(CapturedCreateTailSha256, Convert.ToHexStringLower(SHA256.HashData(GuildRenownBlockCodec.Build(block))));
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
			Assert.Equal(GuildRenownBlockCodec.Length, GuildRenownBlockCodec.Build(block).Length);
		}

		// A guild with no accumulated renown is byte-identical to the fresh-guild scaffold.
		[Fact]
		public void ForRenown_ZeroEqualsFreshGuild()
		{
			Assert.Equal(GuildRenownBlockCodec.Build(GuildRenownBlock.ForFreshGuild()), GuildRenownBlockCodec.Build(GuildRenownBlock.ForRenown(0)));
		}
	}
}
