using System;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the NPC spell-cast form of MagicBuffCodec. NpcSpellCast.bin is a captured ApplySpell
	// (0x6B87E316) carrier content from the hoxxling beach capture: the NPC at handle 0x17E29 casting spell
	// 0x316AA600 at the player (0x1316E71). Rebuilding it from its own fields must reproduce the bytes - so the sim
	// can construct an NPC cast from an NpcSpell row instead of replaying a captured blob.
	public class NpcSpellCastCodecTests
	{
		// The server->client buff-removal carrier is the same ApplySpell shape with the rank-1 "end" spell id
		// and an empty payload. Byte-exact against the mount buff end captured at dismount (mount 0x31eb15, end id
		// 0x31eb1501, self-applied to/from player handle 0x0802e5d4 - NecroL80 *_dismount, frame 1449).
		[Fact]
		public void Build_EmptyPayload_IsTheBuffRemovalEndTwin()
		{
			var capturedCarrier = Convert.FromHexString("6b87e3160000c3500802e5d4000031eb15010000c3500802e5d4");

			var built = MagicBuffCodec.Build(0x0802e5d4, 0x0802e5d4, 0x31eb1501, []);

			Assert.Equal(capturedCarrier, built);
		}

		[Fact]
		public void Build_FromCapturedFields_IsByteIdentical()
		{
			var golden = Golden.Bytes("NpcSpellCast");

			var applyTo = golden.ReadU32Be(8);
			var spellId = golden.ReadU32Be(14);
			var applyFrom = golden.ReadU32Be(22);
			var payload = golden[26..];

			var built = MagicBuffCodec.Build(applyTo, applyFrom, spellId, payload);

			Assert.Equal(golden, built);

			// Parse recovers the fields + data and rebuilds byte-for-byte.
			var rt = MagicBuffCodec.Parse(built);
			Assert.Equal(applyTo, rt.ApplyTo);
			Assert.Equal(applyFrom, rt.ApplyFrom);
			Assert.Equal(spellId, MagicBuffIds.WireIdOf(rt.SpellBaseId, rt.Form));
			Assert.Equal(built, MagicBuffCodec.Build(rt));
		}

		// The mount ride buff (the ApplySpell that seats the player after the horse spawns) built from the
		// MountCatalog constants must reproduce the captured apply byte-for-byte - NecroL80 *_mount_move_dismount,
		// frame 520 (self-applied to/from player handle 0x0802e5d4, spell 0x31EB1500 + the 54-byte payload).
		[Fact]
		public void Build_MountRideBuff_IsByteIdenticalToFrame520()
		{
			var capturedCarrier = Convert.FromHexString("6b87e3160000c3500802e5d4000031eb15000000c3500802e5d4" +
			                                            "00000000000000000083d600000100000001000000000031eb150000005000000000000000000000000000000050000000001560f9bb");

			var built = MagicBuffCodec.Build(0x0802e5d4, 0x0802e5d4, MountCatalog.RideSpell, MountCatalog.RideBuffPayload(80, 0x1560F9BB));

			Assert.Equal(capturedCarrier, built);
		}

		// Retail's world-entry mount restore is the same record as a fresh mount cast. Riding out of the no-mount
		// King Conan castle (NecroL80_..._login_leave_king_conan_castle-Dismount, the ride buff two records after the
		// FullChar snapshot), retail re-seats the mount with an apply that differs from the cast above in only 6 of 84
		// bytes. So there is no "already active" / silent variant of this record to prefer, which is what rules out
		// reconstructing the restore from the snapshot: the snapshot has no buff region, and retail would not use one if
		// it had.
		//
		// Those 6 bytes were long filed as "the per-application effect id and the trailing nonce, which vary by
		// definition". They are nothing of the sort, and decoding them is what turns this pair into evidence for
		// how a restore is shaped:
		//   * bytes 35..37 are the payload's Duration - 8_509_794 cs (23.638 h) against the fresh cast's 8_640_000
		//     (24 h). Retail restores with the remaining time, not the full duration. 21.7 minutes had elapsed.
		//   * bytes 77..79 are the payload's absolute expiry stamp, in milliseconds of the server's uptime.
		// Subtracting each record's own duration from its own stamp recovers the instant each was applied, and the two
		// captures - separate files, separate sessions - land 91.8 seconds apart on one server's uptime: 3.15135 days
		// and 3.15029 days. Two independently captured absolute timestamps agreeing to a minute and a half is what
		// pins the clock, and it is the same clock DispatchPong carries (see MagicBuffExpiryStampTests).
		[Fact]
		public void MountRideBuff_EntryRestoreMatchesAFreshCast_ExceptTheRemainingTimeAndItsExpiry()
		{
			var entryRestore = Convert.FromHexString("6b87e3160000c3500802e5d4000031eb15000000c3500802e5d4" +
			                                         "00000000000000000081d962000100000001000000000031eb15000000500000000000000000000000000000005000000000154bb4d7");

			var built = MagicBuffCodec.Build(0x0802e5d4, 0x0802e5d4, MountCatalog.RideSpell, MountCatalog.RideBuffPayload(80, 0x1560F9BB));

			Assert.Equal(entryRestore.Length, built.Length);
			// Only the Duration (the low three bytes of the u32 spanning 35..37) and the expiry stamp (77..79) may
			// differ - a resumed buff has less time left and a different absolute end. Everything else - spell,
			// endpoints, form byte, the whole 54-byte payload shape - has to match retail's entry restore exactly.
			var differing = Enumerable.Range(0, built.Length).Where(i => built[i] != entryRestore[i]).ToArray();
			Assert.Equal([35, 36, 37, 77, 78, 79], differing);
		}

		[Fact]
		public void Build_NpcIsTheCaster_PlayerIsTheTarget()
		{
			// applyFrom = the NPC handle (the caster), applyTo = the player handle - the tell of an NPC cast.
			var built = MagicBuffCodec.Build(0x1316E71, 0x00017E29, 0x316AA600, []);

			Assert.Equal(0x1316E71u, built.ReadU32Be(8)); // applyTo = player
			Assert.Equal(0x316AA600u, built.ReadU32Be(14)); // spellId
			Assert.Equal(0x00017E29u, built.ReadU32Be(22)); // applyFrom = NPC
		}
	}
}
