using System;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// Cross-player move relay - "other players see me move (and jump)". Ground truth: xxx_2clients_jumping, a
	// two-client session recorded for nothing else. Retail relays a move VERBATIM: the c2s carrier reappears s2c
	// byte-for-byte with only the env byte cleared (0x01 -> 0x00), to the mover's own client AND to the other one.
	// Measured over the whole corpus: 9,438 of 9,932 c2s CharDCMove records have an s2c twin identical bar that byte;
	// in the jumping capture all 35 do, on both streams.
	//
	// This is what locks BroadcastPlayerMove to forwarding the bytes rather than rebuilding them from parsed fields.
	// The rebuild it replaced wrote a fixed [pos][3 floats][tail], which cannot be right for both of the record's forms:
	// flag1 bit4 decides whether the rotation quaternion arrives as four floats or two (docs/re/chardcmove-grammar.md),
	// so in the collapsed form - the common one, and the one a jump uses - it wrote the tail one float out of place.
	// The jump's flag1 survived; the fields it gates did not.
	public class CrossPlayerMoveRelayTests
	{
		// xxx_2clients_jumping, packet idx 1339 (frame 1249): character 0x013184AE jumps. flag1 = 0x56 - the Airborne
		// bit plus the collapsed facing, with the acceleration vector present. Full carrier incl. the [u32 len] prefix.
		private const string CapturedJumpC2S =
			"000000394517aa810000c350013184ae0156443d1031432fc8084427daa03ef577443f60a940040000000040ef42360000000000000000427b94660000";

		// Its s2c twin as the OTHER client received it (idx 1342, frame 1252, the second client's stream): the same
		// bytes with env 0x01 -> 0x00. Byte-identical to the echo the jumper itself got (idx 1340).
		private const string CapturedJumpS2C =
			"000000394517aa810000c350013184ae0056443d1031432fc8084427daa03ef577443f60a940040000000040ef42360000000000000000427b94660000";

		private const uint JumperHandle = 0x013184AE;
		private const uint MapId = 4010; // Tortage Beach

		[Fact]
		public async Task BroadcastPlayerMove_ForwardsTheJumpVerbatim_ToAViewerWhoKnowsTheMover()
		{
			var record = Convert.FromHexString(CapturedJumpC2S);
			var parsed = CharDCMoveCodec.Parse(record);
			Assert.True(((MoveFlag)parsed.Flag1).HasFlag(MoveFlag.Airborne)); // this capture IS a jump

			var sim = new WorldSimulation(null!, null!, null!, NullLogger.Instance);
			var broadcaster = new WorldBroadcaster(sim, null!, NullLogger.Instance, null!);

			var jumper = Place(sim, JumperHandle, parsed.Position);
			var viewer = Place(sim, 0x01317CF8, parsed.Position); // the second client, standing next to them
			var farAway = Place(sim, 0x0131830B, parsed.Position + new Vector3(10_000f, 0f, 0f));
			sim.PlayerWorldEntity(viewer)!.Known.Add(JumperHandle);
			sim.PlayerWorldEntity(jumper)!.Known.Add(0x01317CF8); // already sees the viewer, so this move spawns nobody

			// What the move handler hands us: the carrier minus its [u32 len] prefix, with the env byte cleared.
			var moveBody = record[4..];
			moveBody[12] = 0x00;

			await broadcaster.BroadcastPlayerMove(jumper, moveBody, parsed.Position);

			Assert.Single(viewer.Sent);
			Assert.Empty(jumper.Sent); // the echo to the mover is the handler's own send, not the relay's
			Assert.Empty(farAway.Sent);

			// The relayed packet ends in the captured s2c record, byte for byte - length prefix, flags, acceleration
			// vector and all.
			var expected = Convert.FromHexString(CapturedJumpS2C);
			Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(viewer.Sent[0][^expected.Length..]));
		}

		[Fact]
		public async Task BroadcastPlayerMove_DoesNotRelay_ToAViewerThatHasNotBuiltTheMover()
		{
			var record = Convert.FromHexString(CapturedJumpC2S);
			var parsed = CharDCMoveCodec.Parse(record);

			var sim = new WorldSimulation(null!, null!, null!, NullLogger.Instance);
			var broadcaster = new WorldBroadcaster(sim, null!, NullLogger.Instance, null!);

			var jumper = Place(sim, JumperHandle, parsed.Position);
			var bystander = Place(sim, 0x01317CF8, parsed.Position + new Vector3(10_000f, 0f, 0f));

			await broadcaster.BroadcastPlayerMove(jumper, record[4..], parsed.Position);

			Assert.Empty(jumper.Sent);
			Assert.Empty(bystander.Sent);
		}

		private static RecordingNetworkClient Place(WorldSimulation sim, uint handle, Vector3 position)
		{
			var client = new RecordingNetworkClient(handle);
			var entity = new WorldEntity(handle, MapId, true, $"P{handle:x8}", client);
			sim.Players[client] = entity;
			sim.World.Place(entity, MapId, position, Quaternion.Identity);
			return client;
		}
	}
}
