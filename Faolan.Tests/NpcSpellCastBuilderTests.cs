using System;
using System.Buffers.Binary;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the NPC spell-cast wire builder (#232). NpcSpellCast.bin is a captured NPC-cast ApplySpell
	// (0x6B87E316) carrier content from the hoxxling beach capture: the NPC at handle 0x17E29 casting spell
	// 0x316AA600 at the player (0x1316E71). Rebuilding it from its own fields must reproduce the bytes - so the sim
	// can construct an NPC cast from an NpcSpell row instead of replaying a captured blob.
	public class NpcSpellCastBuilderTests
	{
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
		}

		[Fact]
		public void Build_NpcIsTheCaster_PlayerIsTheTarget()
		{
			// applyFrom = the NPC handle (the caster), applyTo = the player handle - the tell of an NPC cast.
			var built = NpcSpellCastBuilder.Build(applyTo: 0x1316E71, applyFrom: 0x00017E29, spellId: 0x316AA600, payload: []);

			Assert.Equal(0x1316E71u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(8, 4)));   // applyTo = player
			Assert.Equal(0x316AA600u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(14, 4))); // spellId
			Assert.Equal(0x00017E29u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(22, 4))); // applyFrom = NPC
		}
	}
}
