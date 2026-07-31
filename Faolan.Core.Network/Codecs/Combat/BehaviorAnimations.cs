using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The server-INITIATED BehaviorControlCenterIIR forms, as VALUE factories over `BehaviorControlCenterIIR`.
	//
	// Not a codec: nothing here encodes or decodes: each method names one behaviour and returns the record for it, and
	// `BehaviorControlCenterCodec.Build` is what turns it into bytes. That is the same treatment `MoveTaskNodes` got in
	// wave 3, and for the same reason - these forms share parameter lists (`(uint actor, uint targetHandle, string
	// remoteformat)` describes both the sit and the standup), so `Build` overloads could not tell them apart.
	//
	// Every animation's `args` share one protobuf shape: {f1 = animActionId, f3 = <p3>, f5 = 8, f6 = <p6>, then either
	// an f10 target sub-message or an f11 scalar}. The animActionId indexes the client's animation-action table
	// (spellweave trance 42041, chair-sit 42052, standup 42053 all sit in the same run). The tail is behaviour-specific.
	public static class BehaviorAnimations
	{
		// World-object action animations - the actor plays a NAMED animation aimed at a target object, with a feedback
		// remoteformat (decode_sit.py, path-of-azura sit-bench capture). behaviorId 0x2A, seq begin; the anim string +
		// blend constants below are byte-exact vs F2795 (sit) / F2840 (standup). animActionId sits beside the spellweave
		// actions in the same table (42052/42053). Sitting on a bench is startobjectaction(33)|turnon_execute(4).
		public const uint ObjectActionBehaviorId = 0x2A;

		public const uint ObjectStateBehaviorId = 0x12;

		public const ushort SeqBegin = 0x0004;
		public const ushort SeqComplete = 0x000A;

		// The seq the trailing hold-assert uses (F2801); the leading hold-echo uses SeqBegin (F2793).
		public const ushort SeqObjectHold = 0x000F;

		// NPC animation acks are server-initiated, so the echoed sequence is the constant 0x0004 (not a client seq).
		private const ushort NpcAttackAckSeq = 0x0004;

		// The NormalAttack behaviorId carried by an attack BehaviorControlCenterIIR (BehaviorAcks owns the constant -
		// the player-side ack writes the same one).
		private const uint NormalAttackBehaviorId = BehaviorAcks.NormalAttackBehaviorId;

		private const uint IdleBehaviorAction = 41413; // the capture's `08 c5 c3 02` varint
		private const uint IdleBehaviorId = 7;

		private const float SitBlend = 2.6153848f; // 0x40276277 - the sit animation's blend/duration (raw-body BE float)
		private const string SitAnimName = "chair_sit_calm";
		private const string StandAnimName = "standup";
		private const uint BenchSitAction = 6; // the bench's startobjectaction sub-action (turnon_execute)
		private const uint SitAnimAction = 42052;
		private const uint StandAnimAction = 42053;

		// The caster's component-ADD gesture on each weave add - a begin/complete pair carrying the added element
		// (frames 1657/1658): behaviorId 4, args {f1=AddAction, f3=5, f5=8, f6=0, f10=SpellTarget{caster}},
		// tail [1][element][0x01][0][0][0][completeFlag].
		private const uint SpellweaveAddAction = 186463;

		// The caster's trance-EXIT animation on spellweave cancel/fire (cancel-weave capture, frame 141.61): behaviorId
		// 1, args {f1=ExitAction, f3=6, f5=8, f6=0, f11=79}, tail [4][0x27][0][0][0]. Without it the buffs clear but
		// the character stays stuck in the trance pose. Action params are capture-derived (necro).
		private const uint SpellweaveExitAction = 41302;

		// The caster's TRANCE-POSE animation on spellweave ACTIVATE (tools/capture/decode_spellweave.py frame 1645):
		// behaviorId 1, args {f1=TranceAction, f3=4, f5=8, f6=0, f11=1199}, tail [0x27][1][0][0][0]. Without it the
		// trance buffs render but the character stays idle. Action params are capture-derived (necro).
		private const uint SpellweaveTranceAction = 42041;

		// The generic REGISTRATION form (#189/#234/#267): the header plus a `CommandMessage` naming the behaviour
		// slot, its id and its phase, with an optional 20-byte "bound" tail. It is what the create-editor burst
		// streams for every entity it brings into play, and what a scripted NPC pose (Casilda's captive/freed) rides.
		//
		// protobuf-net omits an unset optional and emits fields in ascending number order, so the optional
		// f2/f4/f11/f12 land exactly where the hand-written writer used to put them. `form` is the wire SEQ: 0x0004
		// for the common registration; the rarer 0x0007 subtype adds protobuf field 4 (a varint, between phase and
		// field5). The bound tail (`[u32 boundA][u32 boundB][12 x 00]`) is decoded in
		// tools/capture/decode_behaviorcc.py.
		//
		// Lived as `CreateEditorRecordBuilder.Bcc` until #530 wave 7, which is why the Casilda pose in `Faolan/` had to
		// re-prepend the sub-opcode by hand: the only way to reach it was through a helper that had already sliced it
		// off for the burst.
		public static BehaviorControlCenterIIR Registration(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6, uint? field2 = null, uint? field4 = null,
			uint? field11 = null, uint? field12 = null, uint? boundA = null, uint? boundB = null, ushort form = SeqBegin)
		{
			var message = new CommandMessage
			{
				BccActionId = behaviorId,
				CommandqueueServerId = phase,
				Flags = field5,
				ExecutionStatus = field6
			};
			if (field2 is { } f2)
				message.CommandqueueClientId = f2;
			if (field4 is { } f4)
				message.AnimRandom = f4;
			if (field11 is { } f11)
				message.RechargeTime = f11;
			if (field12 is { } f12)
				message.ActivationTime = f12;

			byte[] tail = [];
			if (boundA is { } a)
			{
				using var bound = new FaolanStream();
				tail = bound.WriteUInt32(a).WriteUInt32(boundB ?? 0u).WriteArray(new byte[12]).ToArray();
			}

			return new BehaviorControlCenterIIR(handle, form, slot, message.ToProtobuf(), tail);
		}

		// The object-action STATE record (behaviorId 0x12) that HOLDS the player in the seated pose. The sit ANIMATION
		// (PlayerSitOnObject) alone plays the sit-down transition then reverts to idle; retail brackets it with this
		// state record (F2793 seq=SeqBegin BEFORE the animation, F2801 seq=SeqObjectHold AFTER) so the client commits to
		// "using" the bench and stays seated until standup. f10 targets the PLAYER; the tail = [objectClass][objectHandle]
		// [action]. Byte-exact vs F2793 / F2801.
		public static BehaviorControlCenterIIR BenchSitState(uint actor, ushort seq, uint benchHandle)
		{
			var args = new CommandMessage
			{
				BccActionId = StandAnimAction,
				CommandqueueClientId = 7,
				CommandqueueServerId = 9,
				Flags = 10,
				ExecutionStatus = 0,
				Target = Target(EntityClass.Character, actor)
			}.ToProtobuf();

			using var tail = new FaolanStream();
			tail.WriteUInt32(EntityClass.SimpleDynel).WriteUInt32(benchHandle).WriteUInt32(BenchSitAction);
			return new BehaviorControlCenterIIR(actor, seq, ObjectStateBehaviorId, args, tail.ToArray());
		}

		// The continuous short-form "AI / idle behavior pulse" retail streams ~10/s for every rendered entity (#225).
		// It is what keeps a standing creature playing its IDLE animation instead of freezing - without it our NPCs and
		// pets stand static. NOT required for rendering (#23), pure fidelity + the seed of the simulation AI layer.
		// Decoded byte-exact from the mount capture (frame 105); `tick` is a MONOTONIC per-stream counter, so the args
		// length grows as its varint widens.
		public static BehaviorControlCenterIIR Heartbeat(uint actor, uint tick)
		{
			var args = new CommandMessage
			{
				BccActionId = IdleBehaviorAction,
				CommandqueueServerId = tick,
				Flags = 0x18,
				ExecutionStatus = 0
			}.ToProtobuf();

			return new BehaviorControlCenterIIR(actor, NpcAttackAckSeq, IdleBehaviorId, args, []);
		}

		// The NPC counter-attack ANIMATION ack: the "second BccAck with actor = target handle" (docs/subsystems/combat.md, was
		// undecoded). Structurally the SAME record as the player's targeted-hit attack ack, but the roles are SWAPPED -
		// top-level actor = the NPC, args.Target + the result-block target = the player it hit - and the args carry NO
		// Mode field (the player-only distinguisher). Sending it makes the client play the NPC's swing animation + the
		// player's hit-react. The damage here is the floating combat number; the authoritative HP move still rides the
		// world-tick StatUpdate (kept separate so we don't double-apply).
		// Decoded in tools/capture/decode_combat_npc_ack.py (98/98 frames); byte-exact against frame 6472 of the strickenbla
		// capture. The animation params (typeId/swingCounter/f5/f11/f12) + the result codes (resultType/resultK1/resultK2)
		// are not semantically decoded - capture-typical defaults for the live send, the exact captured values for the golden.
		public static BehaviorControlCenterIIR NpcAttackAck(uint npcHandle, uint playerHandle, uint damage, uint typeId, uint swingCounter, uint f5 = 24, uint f11 = 64, uint f12 = 56,
			byte resultType = 0xCB, uint resultK1 = 1, uint resultK2 = 2)
		{
			// args protobuf: {f1=TypeId, f3=swingCounter, f5, f6=0, f10=Target{f1=0xC350, f2=player}, f11, f12}.
			// NO f2/Mode - that field is present only on player-initiated actions and is the NPC-vs-player tell.
			// CommandMessage field 10 is the TARGET - a ProtoID32 { type, instance }, not the "spell target" the old
			// hand-reconstructed schema called it: its first word is an entity class (Gitea #387).
			var args = new CommandMessage
			{
				BccActionId = typeId,
				CommandqueueServerId = swingCounter,
				Flags = f5,
				ExecutionStatus = 0,
				Target = Target(EntityClass.Character, playerHandle),
				RechargeTime = f11,
				ActivationTime = f12
			}.ToProtobuf();

			// The result block is BehaviorAcks' - the player's targeted-hit ack sends the same 132 bytes with the roles
			// swapped, and it was written out in both files until #530 wave 7.
			return new BehaviorControlCenterIIR(npcHandle, NpcAttackAckSeq, NormalAttackBehaviorId, args, BehaviorAcks.HitResultBlock(playerHandle, damage, resultType, resultK1, resultK2));
		}

		// Sit the actor on a world object (a bench): plays "chair_sit_calm" aimed at the object. `remoteformat` is the
		// object-action feedback descriptor (SitRemoteformat, or a captured literal in tests). Byte-exact vs F2795.
		public static BehaviorControlCenterIIR PlayerSitOnObject(uint actor, uint targetHandle, string remoteformat)
		{
			return ObjectActionAnimation(actor, SitAnimAction, 10, 0, EntityClass.SimpleDynel, targetHandle, SitAnimName, 2, 0x86, SitBlend, remoteformat, 0x4C);
		}

		// Stand the actor back up. Sent in answer to the CLIENT's standup request (behaviour 1, phase 2) - not inferred
		// from movement, which is what we used to do; see the behaviour-1 branch in GameServerListener.Inbound.
		// Byte-exact vs F2840.
		public static BehaviorControlCenterIIR PlayerStandUp(uint actor, uint targetHandle, string remoteformat)
		{
			return ObjectActionAnimation(actor, StandAnimAction, 12, 20, EntityClass.SimpleDynel, targetHandle, StandAnimName, 0, 0, 0f, remoteformat, 0x01);
		}

		// The sit feedback remoteformat: token = startobjectaction(33)|turnon_execute(4)|<objectClass>, category 50026,
		// key = the object's localized action key ("" when we haven't imported it - the key drives the subtitle lookup,
		// not the animation).
		public static string SitRemoteformat(string objectClass, string key = "")
		{
			return $"<remoteformat token=\"startobjectaction(33)|turnon_execute(4)|{objectClass}\" category=\"50026\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}

		public static BehaviorControlCenterIIR SpellweaveComponentAdd(uint caster, uint elementBaseId, bool complete)
		{
			var message = ArgsPrefix(SpellweaveAddAction, 5, 0);
			message.Target = Target(EntityClass.Character, caster);

			using var tail = new FaolanStream();
			tail.WriteUInt32(1).WriteUInt32(elementBaseId).WriteByte(0x01).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).WriteByte(complete);

			return new BehaviorControlCenterIIR(caster, complete
				? SeqComplete
				: SeqBegin, 4, message.ToProtobuf(), tail.ToArray());
		}

		public static BehaviorControlCenterIIR SpellweaveTrance(uint caster)
		{
			var message = ArgsPrefix(SpellweaveTranceAction, 4, 0);
			message.RechargeTime = 1199;

			using var tail = new FaolanStream();
			tail.WriteUInt32(0x27).WriteUInt32(1).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0);

			return new BehaviorControlCenterIIR(caster, SeqBegin, 1, message.ToProtobuf(), tail.ToArray());
		}

		public static BehaviorControlCenterIIR SpellweaveTranceExit(uint caster)
		{
			var message = ArgsPrefix(SpellweaveExitAction, 6, 0);
			message.RechargeTime = 79;

			using var tail = new FaolanStream();
			tail.WriteUInt32(4).WriteUInt32(0x27).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0);

			return new BehaviorControlCenterIIR(caster, SeqBegin, 1, message.ToProtobuf(), tail.ToArray());
		}

		// The standup feedback remoteformat: shows "Standing up" as the on-screen feedback.
		public static string StandRemoteformat()
		{
			return "<remoteformat id=\"0\" category=\"0\" key=\"\" knubot=\"0\"  ><parameter value=\"7;Standing up\" /></remoteformat>";
		}

		// The args every animation shares: a CommandMessage {bcc_action_id, commandqueue_server_id, flags=8,
		// execution_status}. Callers set Target (field 10) or RechargeTime (field 11) before serializing.
		private static CommandMessage ArgsPrefix(uint animActionId, uint p3, uint p6)
		{
			return new CommandMessage
			{
				BccActionId = animActionId,
				CommandqueueServerId = p3,
				Flags = 8,
				ExecutionStatus = p6
			};
		}

		// A world-object action animation: the actor plays `animName` aimed at a target object, carrying a feedback
		// remoteformat. tail = [u32 a][u16 nameLen][animName][u32 b][f32 blend][4x00][u16 rfLen][remoteformat][u32 trailer].
		private static BehaviorControlCenterIIR ObjectActionAnimation(uint actor, uint animActionId, uint p3, uint p6, uint targetClass, uint targetHandle, string animName, uint tailA,
			uint tailB, float blend, string remoteformat, uint trailer)
		{
			var message = ArgsPrefix(animActionId, p3, p6);
			message.Target = Target(targetClass, targetHandle);

			using var tail = new FaolanStream();
			tail
				.WriteUInt32(tailA)
				.WriteString(animName)
				.WriteUInt32(tailB)
				.WriteFloat(blend)
				.WriteArray(new byte[4])
				// The remoteformat is a LENGTH-PREFIXED string, exactly like animName above - not a "flag" byte followed
				// by loose text, which is how this used to be written. The two captured records prove it: the sit's
				// prefix is 0xAA (170) and its string is 170 bytes, the standup's is 0x84 (132) for a 132-byte string.
				// Those lengths were baked in as constants, so every GENERATED remoteformat (ours differ from the capture's
				// - we emit key="" until the object's localized action key is imported) shipped a prefix that overstated
				// the string. The client then read past the end and dropped the whole record:
				//   "Figos ID 0 sent a too short IIR UNKNOWN Key) ... datasize left in stream=141, raw=217"
				// which is why sitting on a bench played nothing and the character stood straight back up.
				// WriteString emits [u16 len][latin1], and 4 zeros + a u16 length is byte-identical to the 5 zeros + u8
				// this replaces for any string under 256 bytes - so the captured goldens still match to the byte.
				.WriteString(remoteformat)
				.WriteUInt32(trailer);

			return new BehaviorControlCenterIIR(actor, SeqBegin, ObjectActionBehaviorId, message.ToProtobuf(), tail.ToArray());
		}

		// Field 10 - who/what the animation is aimed at. A ProtoID32 { type, instance }; the old model called it a
		// "spell target" with a spell_id first word, which it never was (Gitea #387).
		private static ProtoID32 Target(uint entityClass, uint instance)
		{
			return new ProtoID32
			{
				Type = entityClass,
				Instance = instance
			};
		}
	}
}
