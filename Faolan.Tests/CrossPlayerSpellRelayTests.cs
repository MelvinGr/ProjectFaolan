using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// Cross-player ApplySpell relay - the "other players see my spell EFFECT" milestone. Ground truth: the
	// other-players-visible capture (Hoxxling ...other_playersvisible) streams other players' ApplySpell (0x6B87E316)
	// to observers who can see them - e.g. player 0x01316e68 self-buffs and observer 0x01316e71 (who has built that
	// player's handle) receives the exact carrier, framed to the observer's endpoint. This locks BroadcastSpellToViewers:
	// forward that body verbatim to every viewer who Knows the caster, and to nobody else (the caster isn't echoed, and
	// a client that hasn't built the caster's handle - #245 - is skipped).
	public class CrossPlayerSpellRelayTests
	{
		// A real other-player self-buff ApplySpell body from the capture (idx 3735, actor 0x01316e68), WITHOUT the
		// [u32 length] prefix that the DispatchPassBlob array framing re-adds.
		private const string CapturedSelfBuffBody =
			"6b87e3160000c35001316e6800003a645c000000c35001316e6800000000000000000041eb0000010000000100000000003a645c00000006000000000000000000000000000000060000000003400f65";

		private const uint CasterHandle = 0x01316e68; // the acting player (applyFrom/applyTo in the captured body)
		private const uint MapId = 4010; // Tortage Beach

		[Fact]
		public async Task BroadcastSpellToViewers_ForwardsTheApplySpellVerbatim_ToViewersWhoKnowTheCaster_AndNooneElse()
		{
			var sim = new WorldSimulation(null!, null!, null!, NullLogger.Instance);
			var broadcaster = new WorldBroadcaster(sim, null!, NullLogger.Instance, null!);

			var caster = Place(sim, CasterHandle);
			var viewer = Place(sim, 0x01316e71); // the observer in the capture - has built the caster
			var stranger = Place(sim, 0x01316d99); // in the zone but has NOT built the caster's handle

			sim.PlayerWorldEntity(viewer).Known.Add(CasterHandle);

			var body = Convert.FromHexString(CapturedSelfBuffBody);
			await broadcaster.BroadcastSpellToViewers(caster, body);

			// Exactly one relay, to the viewer who knows the caster.
			Assert.Single(viewer.Sent);
			Assert.Empty(caster.Sent); // never echoed back to the caster (its own send happens on the caster path)
			Assert.Empty(stranger.Sent); // #245: not to a client that never built the caster's handle

			// The relayed packet carries the captured ApplySpell carrier verbatim, length-prefixed (DispatchPassBlob).
			var expectedTail = new byte[4 + body.Length];
			BinaryPrimitives.WriteUInt32BigEndian(expectedTail, (uint)body.Length);
			body.CopyTo(expectedTail, 4);

			var sent = viewer.Sent[0];
			Assert.Equal(expectedTail, sent[^expectedTail.Length..]);
		}

		[Fact]
		public async Task BroadcastSpellToViewers_IsANoOp_WhenNoOneSeesTheCaster()
		{
			var sim = new WorldSimulation(null!, null!, null!, NullLogger.Instance);
			var broadcaster = new WorldBroadcaster(sim, null!, NullLogger.Instance, null!);

			var caster = Place(sim, CasterHandle);
			var bystander = Place(sim, 0x01316e71); // present but has NOT built the caster

			await broadcaster.BroadcastSpellToViewers(caster, Convert.FromHexString(CapturedSelfBuffBody));

			Assert.Empty(caster.Sent);
			Assert.Empty(bystander.Sent);
		}

		private static RecordingNetworkClient Place(WorldSimulation sim, uint handle)
		{
			var client = new RecordingNetworkClient(handle);
			var entity = new WorldEntity(handle, MapId, true, $"P{handle:x8}", client);
			sim.Players[client] = entity;
			sim.World.Place(entity, MapId, Vector3.Zero, Quaternion.Identity);
			return client;
		}
	}
}
