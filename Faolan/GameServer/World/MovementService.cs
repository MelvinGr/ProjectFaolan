using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The player move (<c>CharDCMoveIIR</c>): parse the client's move, accept or pin it, commit the live
	///     transform, run everything a move can trigger, echo it back and relay it to whoever sees the mover.
	/// </summary>
	/// <remarks>
	///     What a move can trigger: standing up from a seat, reach goals, an NPC approach, fall damage and hazards, and
	///     the cast cancel. The echo is the client's own bytes with the env byte cleared - see
	///     <see cref="HandleMove" />.
	/// </remarks>
	public sealed class MovementService(WorldSimulation simulation, WorldObjectService worldObjects, QuestProgressionService questProgression,
		ConversationService conversation, EnvironmentService environment, WorldBroadcaster broadcaster)
	{
		private readonly WorldSimulation _simulation = simulation;
		private readonly WorldObjectService _worldObjects = worldObjects;
		private readonly QuestProgressionService _questProgression = questProgression;
		private readonly ConversationService _conversation = conversation;
		private readonly EnvironmentService _environment = environment;
		private readonly WorldBroadcaster _broadcaster = broadcaster;

		/// <summary>
		///     Squared world distance a move must cover to count as a real translation that interrupts an in-flight
		///     cast.
		/// </summary>
		/// <remarks>
		///     Above pure-rotation jitter - turning in place leaves the position unchanged - but well under one walk
		///     step, so any genuine step cancels the cast while aiming a ground-target reticle does not.
		/// </remarks>
		private const float CastMoveCancelEpsilonSq = 0.04f; // (0.2 world units)^2

		/// <param name="body">The open stream, positioned just past the IIR type word.</param>
		/// <param name="rawPayload">
		///     The whole IIR carrier body, leading <c>[u32 len]</c> included, because the echo is the client's own
		///     bytes with a single byte flipped.
		/// </param>
		public async Task HandleMove(INetworkClient client, FaolanStream body, byte[] rawPayload)
		{
		body.ReadUInt32(); // 0xC350 IIR marker
		body.ReadUInt32(); // the mover's handle - we key off the session, not the client's claim

		// [u8 env][u8 flag1] - a layout bitfield, not a movement-type enum: every value is legal, so there is
		// nothing to switch on (docs/re/chardcmove-grammar.md). Switching on it drops roughly a quarter of moves.
		var movingType = body.ReadUInt16();
		var moveFlags = (MoveFlag)(movingType & 0xFF);

		var sessionCharacter = client.Session.Character!;

		var movePosition = body.ReadVector3();

		// The floats after the position are the entity's rotation quaternion, not Euler angles; bit 4 says how
		// many are on the wire - set, which is most moves, sends only the (qy, qw) an upright yaw-only facing
		// needs, clear sends all four.
		var moveRotation = moveFlags.HasFlag(MoveFlag.CollapsedRotation)
			? new Quaternion(0f, body.ReadFloat(), 0f, body.ReadFloat())
			: body.ReadQuaternion();

		// The client's own velocity, which is what fall damage is resolved from. The c2s move is the same
		// record as the s2c one - retail's relay is the c2s body with its env byte flipped, byte for byte, see
		// the echo below - so the tail is CharDCMoveIIR's: [u8 flag2] and then, when flag2 < 8, the optional
		// vectors the flag1 bits gate, velocity first.
		//
		// Read off the already-open stream rather than through the codec, which wants a whole body with its own
		// header; it is the same grammar either way.
		float? moveVerticalSpeed = null;
		if (body.Position < body.Length)
		{
			var moveFlag2 = body.ReadByte();
			if (moveFlag2 < 8 && !moveFlags.HasFlag(MoveFlag.NoVelocityVector) && body.Length - body.Position >= 12)
				moveVerticalSpeed = body.ReadVector3().Y;
		}

		// The WorldEntity owns the live transform; the persisted Character is only the spawn seed.
		//
		// The `?? sessionCharacter` fallbacks below carry the create-editor connection, which streams
		// CharDCMove while the player turns their avatar in the editor (first c2s move at fr113/116 in the
		// Barbarian-create and Blablatest captures, long after the spawn record at fr51/64) and never reaches
		// RegisterPlayerInWorld, which only runs on world entry. There is no entity to hold the transform, so
		// the session Character is the right place: for an editor avatar the character row is the live state,
		// and it is what the world entry that follows spawns from.
		var moveEntity = _simulation.PlayerWorldEntity(client);

		// The pre-move position, so a real translation below can interrupt an in-flight cast.
		var previousPosition = moveEntity?.Position ?? sessionCharacter.Position;

		// A fallback only: a seated player who leaves the seat without asking - a teleport, or a client that
		// skips the request - still gets stood up. The normal path is the client's own standup request,
		// behaviour 1 in the BCC branch. Gated on distance, because a seated client streams position syncs
		// that must not stand it up.
		await _worldObjects.StandUpIfMoved(client, movePosition);

		// The spellweave trance immobilises the caster: while IsSpellweaving the server pins the position,
		// rejecting translation, but still accepts rotation. This handler is the enforcement point, and the
		// relay below carries the pinned position so other players see the caster rooted.
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

		// A move can satisfy a ReachArea quest goal, such as Shipwrecked's "Enter the jungle": solve any whose
		// area this position falls in. An in-memory distance test, with the targets cached on the entity.
		if (moveEntity != null)
			await _questProgression.CheckReachGoals(client, moveEntity, livePosition);

		if (moveEntity != null)
			await _conversation.CheckApproach(client, moveEntity, livePosition);

		// The world's own damage sources. Fall damage is resolved here rather than on the world tick because a
		// fall's size is only known on the record that reports the landing, and a hazard's client-named
		// trigger is `playerenter`, the crossing itself. It runs on the accepted position, so a rooted
		// spellweaver cannot be walked into lava by a move the server rejected, and it needs no previous
		// position - the entity's is already committed above, and the fall's apex is seeded from
		// WorldEntity.LastGroundedY, which the service maintains itself.
		if (moveEntity != null)
			await _environment.OnPlayerMoved(client, moveEntity, livePosition, moveFlags, moveVerticalSpeed);

		// Moving cancels an in-flight cast: a real translation invalidates the deferred resolve via the cast
		// token. Turning in place leaves the position unchanged, so aiming a ground-target reticle never
		// self-cancels, and a pinned spellweave (delta 0) cannot cancel its own trance. The cast bar is the
		// client's own business; it sends the cancel BCC handled above.
		if (Vector3.DistanceSquared(previousPosition, livePosition) > CastMoveCancelEpsilonSq)
			client.Session.ActiveCastToken++;

		// Echo the move back verbatim with only the env byte cleared. Retail's s2c CharDCMove is the c2s move
		// with its env byte flipped 0x01 -> 0x00, same length and same body, not a reconstruction: rebuilding
		// it from the parsed position and rotation produces a body the client's CharDCMoveIIR reader rejects
		// ("ReadSubClass failed ... Blob data may be inconsistant" in the client log), after which it falls
		// back to prediction and the movement jitters.
		//
		// The echo goes out even while the player is seated, as retail does: in the sit capture the seated
		// player's c2s CharDCMove is answered with an s2c one (pkt 6812 -> 6815, 6841 -> 6842) and the standup
		// follows. Suppressing it deadlocks standing up, because standup needs the player to have moved
		// SeatLeaveDistance from the seat and a client with no answer to its first move stops sending any.
		//
		// While spellweaving the position is pinned: the client does not self-root, so the echoed position
		// floats are overwritten with the accepted position. Otherwise the client's own move rides back
		// unchanged.
		var echoBody = rawPayload[4..]; // drop the u32 iirLength: [opcode][c350][inst][env][flag1][pos][...]

		// The movingType u16 is [u8 env][u8 flag1] (docs/re/chardcmove-grammar.md): the client streams its move
		// with env=0x01, the "I am the controlling client" flag, and retail's echo and relay both carry
		// env=0x00 - byte-exact in the other-players-visible capture, where the c2s move and its s2c twin
		// differ only in this byte (c2s 0x011e -> s2c 0x001e). Forwarding the client's 0x01 makes other
		// viewers treat the move as locally controlled and over-predict it from the velocity block, so the
		// mover drifts and teleports even while standing still. flag1, the layout bitfield with the jump bit
		// in it, rides through untouched.
		echoBody[12] = 0x00;
		if (client.Session.IsSpellweaving && echoBody.Length >= 26)
		{
			echoBody.WriteF32Be(14, acceptedPosition.X);
			echoBody.WriteF32Be(18, acceptedPosition.Y);
			echoBody.WriteF32Be(22, acceptedPosition.Z);
		}

		await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
			s => s.WriteArrayUInt32Length(echoBody)));

		// Interest-managed broadcast: spawn this player to newly-near players and forward the move to those who
		// already see them, carrying the same body the echo just did. A no-op with nobody else in range.
		await _broadcaster.BroadcastPlayerMove(client, echoBody, livePosition);

		}
	}
}
