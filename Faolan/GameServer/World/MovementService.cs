using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;
using Faolan.GameServer;

namespace Faolan.GameServer.World
{
	// The player MOVE (CharDCMoveIIR): parse the client's move, accept or pin it (the trance root), commit the live
	// transform, run everything a move can trigger (stand-up, reach goals, NPC approach, fall damage and hazards,
	// the cast cancel), echo it back verbatim with the env byte cleared and relay it to whoever sees the mover. The
	// listener's biggest inbound case until #928 item 11; the wire notes are the case's own.
	public sealed class MovementService(WorldSimulation simulation, WorldObjectService worldObjects, QuestProgressionService questProgression,
		ConversationService conversation, EnvironmentService environment, WorldBroadcaster broadcaster)
	{
		private readonly WorldSimulation _simulation = simulation;
		private readonly WorldObjectService _worldObjects = worldObjects;
		private readonly QuestProgressionService _questProgression = questProgression;
		private readonly ConversationService _conversation = conversation;
		private readonly EnvironmentService _environment = environment;
		private readonly WorldBroadcaster _broadcaster = broadcaster;

		// Squared world-distance a move must cover to count as a real TRANSLATION that interrupts an in-flight cast
		// (#347). Above pure-rotation jitter (turning in place leaves the position unchanged) but well under one walk
		// step, so any genuine step cancels the cast while aiming a ground-target reticle does not.
		private const float CastMoveCancelEpsilonSq = 0.04f; // (0.2 world units)^2

		// `body` is the open stream positioned just past the IIR type word; `rawPayload` is the whole IIR carrier body
		// (the [u32 len] and all), because the echo is the client's bytes with one flipped.
		public async Task HandleMove(INetworkClient client, FaolanStream body, byte[] rawPayload)
		{
		body.ReadUInt32(); // 0xC350 IIR marker
		body.ReadUInt32(); // the mover's handle - we key off the session, not the client's claim

		// [u8 env][u8 flag1] - a LAYOUT BITFIELD, not a movement-type enum: every value is legal, so there
		// is nothing to switch on (docs/re/chardcmove-grammar.md; switching dropped 24% of moves).
		var movingType = body.ReadUInt16();
		var moveFlags = (MoveFlag)(movingType & 0xFF);

		var sessionCharacter = client.Session.Character!;

		var movePosition = body.ReadVector3();

		// The floats after the position are the entity's rotation QUATERNION; bit4 says how many are on the
		// wire - set (most moves) sends only the (qy, qw) an upright yaw-only facing needs, clear sends all
		// four. Reading them as Euler degrees pinned every character's facing at ~identity.
		var moveRotation = moveFlags.HasFlag(MoveFlag.CollapsedRotation)
			? new Quaternion(0f, body.ReadFloat(), 0f, body.ReadFloat())
			: body.ReadQuaternion();

		// #515: the client's own VELOCITY, which is what fall damage is resolved from. The c2s move is the
		// same record as the s2c one - retail's relay is the c2s body with its env byte flipped, verified
		// byte-exact (see the echo below) - so the tail is CharDCMoveIIR's: `[u8 flag2]` and then, when
		// flag2 < 8, the optional vectors the flag1 bits gate, velocity first.
		//
		// Read here rather than through CharDCMoveCodec because this handler works off the already-open
		// stream and the codec wants a whole body with its own header; it is the same grammar either way.
		float? moveVerticalSpeed = null;
		if (body.Position < body.Length)
		{
			var moveFlag2 = body.ReadByte();
			if (moveFlag2 < 8 && !moveFlags.HasFlag(MoveFlag.NoVelocityVector) && body.Length - body.Position >= 12)
				moveVerticalSpeed = body.ReadVector3().Y;
		}

		// The WorldEntity owns the live transform (Gitea #404); the persisted Character is only the spawn seed.
		//
		// The `?? sessionCharacter` fallbacks below are NOT dead (the TODOs asking that are now answered):
		// they carry the CREATE-EDITOR connection. That connection streams CharDCMove while the player turns
		// their avatar in the editor - the captures show its first c2s move at fr113/116 (Barbarian-create,
		// Blablatest), long after its spawn record at fr51/64 - and it never reaches RegisterPlayerInWorld,
		// which only runs on world entry. So there is no entity to hold the transform, and writing it to the
		// session Character is right there: for an editor avatar the character row IS the live state, and it
		// is what the world entry that follows will spawn from.
		var moveEntity = _simulation.PlayerWorldEntity(client);

		// The pre-move position, so a real TRANSLATION below can interrupt an in-flight cast (#347).
		var previousPosition = moveEntity?.Position ?? sessionCharacter.Position;

		// FALLBACK only: a seated player who leaves the seat WITHOUT asking (a teleport, or a client that
		// skips the request) still gets stood up - the normal path is the client's own standup request
		// (behaviour 1, in the BCC branch). Gated on distance, because a seated client streams position
		// syncs that must not stand it up.
		await _worldObjects.StandUpIfMoved(client, movePosition);

		// Trance root (#141): the spellweave trance immobilises the caster, so while IsSpellweaving the
		// server PINS the position (rejects translation) but still accepts rotation - the move handler is
		// the enforcement point. Others then see the caster rooted; the relay below carries the pinned pos.
		var acceptedPosition = client.Session.IsSpellweaving
			? moveEntity?.Position ?? sessionCharacter.Position
			: movePosition;

		if (moveEntity != null)
		{
			moveEntity.Position = acceptedPosition;
			moveEntity.Rotation = moveRotation;
		}
		else
		{
			sessionCharacter.Position = acceptedPosition;
			sessionCharacter.Rotation = moveRotation;
		}

		var livePosition = moveEntity?.Position ?? sessionCharacter.Position;

		// #262: a move can satisfy a ReachArea quest goal (e.g. Shipwrecked's "Enter the jungle") - solve
		// any whose area this position falls in. In-memory distance test (targets cached on the entity).
		if (moveEntity != null)
			await _questProgression.CheckReachGoals(client, moveEntity, livePosition);

		if (moveEntity != null)
			await _conversation.CheckApproach(client, moveEntity, livePosition); // #523

		// #495/#504: the world's own damage sources. Fall damage lands HERE rather than on the world tick
		// because a fall's size is only known on the record that reports the landing, and a hazard's client-
		// named trigger is `playerenter` - i.e. the crossing. Runs on the ACCEPTED position, so a rooted
		// spellweaver cannot be walked into lava by a move the server rejected. It reads no "previous
		// position" (the entity's is already committed above): the fall's apex is seeded from
		// WorldEntity.LastGroundedY, which the service maintains itself.
		if (moveEntity != null)
			await _environment.OnPlayerMoved(client, moveEntity, livePosition, moveFlags, moveVerticalSpeed);

		// Moving cancels an in-flight cast (#347): a real TRANSLATION invalidates the deferred Resolve()
		// via the cast token. Turning in place leaves the position unchanged, so aiming a ground-target
		// reticle never self-cancels, and a pinned spellweave (delta 0) can't cancel its own trance. The
		// cast BAR is the client's own business - it sends the cancel BCC handled above.
		if (Vector3.DistanceSquared(previousPosition, livePosition) > CastMoveCancelEpsilonSq)
			client.Session.ActiveCastToken++;

		// NB moves are echoed even while SEATED, because retail does: in the sit capture the seated player's
		// c2s CharDCMove is answered with an s2c one (pkt 6812 -> 6815, 6841 -> 6842) and the standup
		// follows. This used to be suppressed on the observation that echoing "cancels the sit pose" -
		// true at the time, but the cause was the sit records themselves being REJECTED by the client
		// (the hold-assert's remoteformat length prefix, c6e015f3, and the over-padded ack tail,
		// 063a5a83), so the pose never committed and any move looked like it cancelled the sit.
		//
		// Suppressing it also deadlocked standing up: standup needs the player to have moved
		// SeatLeaveDistance from the seat, but with no echo the client got no answer to its first move
		// and stopped sending any, so the threshold could never be reached and only the client's own
		// local timeout got the player up. (It also skipped BroadcastPlayerMove entirely, so world
		// objects did not stream while seated either.)
		// Echo the move back VERBATIM with only the env byte cleared - retail's s2c CharDCMove IS the
		// c2s move with its env byte flipped 0x01 -> 0x00 (same length, same body), NOT a reconstruction.
		// Rebuilding it from the parsed pos/rot (round-tripping the rotation through a quaternion)
		// produced a body the client's CharDCMoveIIR reader REJECTS ("ReadSubClass failed ... Blob data
		// may be inconsistant" in the client log), so it fell back to prediction -> the movement jitter
		// (#272). While spellweaving the position is PINNED (trance root #141) - the client does NOT self-root
		// (verified: without this overwrite you can walk during a weave), so overwrite the echoed position
		// floats with the accepted (pinned) position; otherwise the client's own move rides back unchanged.
		var echoBody = rawPayload[4..]; // drop the u32 iirLength: [opcode][c350][inst][env][flag1][pos][...]

		// The movingType u16 is [u8 env][u8 flag1] (docs/re/chardcmove-grammar.md): the client streams
		// its move with env=0x01 (the "I am the controlling client" flag) and retail's echo AND relay
		// both carry env=0x00 - verified byte-exact in the other-players-visible capture, where the c2s
		// move and its s2c twin differ ONLY in this byte (c2s 0x011e -> s2c 0x001e). Forwarding the
		// client's 0x01 verbatim made OTHER viewers treat B's move as locally-controlled and
		// over-predict it from the velocity block, so B drifted/teleported even while standing still
		// (#48). flag1 (the layout bitfield, jump bit included) rides through untouched.
		echoBody[12] = 0x00;
		if (client.Session.IsSpellweaving && echoBody.Length >= 26)
		{
			echoBody.WriteF32Be(14, acceptedPosition.X);
			echoBody.WriteF32Be(18, acceptedPosition.Y);
			echoBody.WriteF32Be(22, acceptedPosition.Z);
		}

		await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
			s => s.WriteArrayUInt32Length(echoBody)));

		// Interest-managed broadcast: spawn this player to newly-near players and forward the move to those
		// who already see them (Gitea #404), carrying the same body the echo just did. No-op solo.
		await _broadcaster.BroadcastPlayerMove(client, echoBody, livePosition);

		}
	}
}
