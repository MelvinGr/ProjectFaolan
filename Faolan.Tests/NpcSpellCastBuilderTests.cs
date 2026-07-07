using System;
using System.Buffers.Binary;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Xunit;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Tests
{
	// Byte-exact gate for the NPC spell-cast wire builder (#232). NpcSpellCast.bin is a captured NPC-cast ApplySpell
	// (0x6B87E316) carrier content from the hoxxling beach capture: the NPC at handle 0x17E29 casting spell
	// 0x316AA600 at the player (0x1316E71). Rebuilding it from its own fields must reproduce the bytes - so the sim
	// can construct an NPC cast from an NpcSpell row instead of replaying a captured blob.
	public class NpcSpellCastBuilderTests
	{
		// #253: the server->client buff-REMOVAL carrier is the same ApplySpell shape with the rank-1 "end" spell id
		// and an EMPTY payload. Byte-exact against the mount buff end captured at dismount (mount 0x31eb15, end id
		// 0x31eb1501, self-applied to/from player handle 0x0802e5d4 - NecroL80 *_dismount, frame 1449).
		[Fact]
		public void Build_EmptyPayload_IsTheBuffRemovalEndTwin()
		{
			var capturedCarrier = Convert.FromHexString("6b87e3160000c3500802e5d4000031eb15010000c3500802e5d4");

			var built = NpcSpellCastBuilder.Build(0x0802e5d4, 0x0802e5d4, 0x31eb1501, []);

			Assert.Equal(capturedCarrier, built);
		}

		[Fact]
		public void Build_FromCapturedFields_IsByteIdentical()
		{
			var golden = Golden.Bytes("NpcSpellCast");

			var applyTo = BinaryPrimitives.ReadUInt32BigEndian(golden.AsSpan(8, 4));
			var spellId = BinaryPrimitives.ReadUInt32BigEndian(golden.AsSpan(14, 4));
			var applyFrom = BinaryPrimitives.ReadUInt32BigEndian(golden.AsSpan(22, 4));
			var payload = golden[26..];

			var built = NpcSpellCastBuilder.Build(applyTo, applyFrom, spellId, payload);

			Assert.Equal(golden, built);

			// ICodec round-trip: Read recovers the fields + payload and rebuilds byte-for-byte.
			var rt = NpcSpellCastBuilder.NpcSpellCast.Read(new Faolan.Core.FaolanStream(built));
			Assert.Equal(applyTo, rt.ApplyTo);
			Assert.Equal(applyFrom, rt.ApplyFrom);
			Assert.Equal(spellId, rt.SpellId);
			Assert.Equal(built, rt.ToArray());
		}

		// #26: the mount RIDE buff (the ApplySpell that seats the player after the horse spawns) built from the
		// MountCatalog constants must reproduce the captured apply byte-for-byte - NecroL80 *_mount_move_dismount,
		// frame 520 (self-applied to/from player handle 0x0802e5d4, spell 0x31EB1500 + the 54-byte payload).
		[Fact]
		public void Build_MountRideBuff_IsByteIdenticalToFrame520()
		{
			var capturedCarrier = Convert.FromHexString("6b87e3160000c3500802e5d4000031eb15000000c3500802e5d4" + "00000000000000000083d600000100000001000000000031eb150000005000000000000000000000000000000050000000001560f9bb");

			var built = NpcSpellCastBuilder.Build(0x0802e5d4, 0x0802e5d4, MountCatalog.RideSpell, MountCatalog.RideBuffPayload());

			Assert.Equal(capturedCarrier, built);
		}

		[Fact]
		public void Build_NpcIsTheCaster_PlayerIsTheTarget()
		{
			// applyFrom = the NPC handle (the caster), applyTo = the player handle - the tell of an NPC cast.
			var built = NpcSpellCastBuilder.Build(0x1316E71, 0x00017E29, 0x316AA600, []);

			Assert.Equal(0x1316E71u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(8, 4))); // applyTo = player
			Assert.Equal(0x316AA600u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(14, 4))); // spellId
			Assert.Equal(0x00017E29u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(22, 4))); // applyFrom = NPC
		}
	}
}
