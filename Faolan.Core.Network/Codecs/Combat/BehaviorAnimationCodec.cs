using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Server-originated BehaviorControlCenterIIR (0x642CD3D6) ANIMATIONS - the generic behavior channel that makes a
	// client play an actor's animation/pose. The same channel carries combat swings/casts (CombatPacketCodec) and NPC
	// swing acks; this file is the home for the server-INITIATED animation packets (not echo-acks of client behaviors)
	// as more subsystems need to drive a pose. Each builder returns the record bytes AFTER the 0x2000 carrier's u32
	// length prefix (the caller wraps them in the world-framed IIR carrier), like PetIirCodec.
	//
	// Common BCC framing: [op][0xC350][actor][u16 seq][u32 behaviorId][u32 argsLen][args protobuf][trailer]. `seq`
	// distinguishes begin (0x0004) from complete (0x000A) - the same form-4 -> form-0x0A pairing as a cast. Every
	// animation's `args` share the same protobuf shape: {f1=animActionId, f3=<p3>, f5=8, f6=<p6>, then either an f10
	// target sub-message or an f11 scalar}. The animActionId indexes the client's animation-action table (spellweave
	// trance 42041, chair-sit 42052, standup 42053 all sit in the same run). The trailer is behavior-specific.
	public static class BehaviorAnimationCodec
	{
		// World-object action animations - the actor plays a NAMED animation aimed at a target object, with a feedback
		// remoteformat (decode_sit.py, path-of-azura sit-bench capture). behaviorId 0x2A, seq begin; the anim string +
		// blend constants below are byte-exact vs F2795 (sit) / F2840 (standup). animActionId sits beside the spellweave
		// actions in the same table (42052/42053). Sitting on a bench is startobjectaction(33)|turnon_execute(4).
		public const uint ObjectActionBehaviorId = 0x2A;

		public const uint ObjectStateBehaviorId = 0x12;

		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR;

		public const ushort SeqBegin = 0x0004;
		public const ushort SeqComplete = 0x000A;

		// The seq the trailing hold-assert uses (F2801); the leading hold-echo uses SeqBegin (F2793).
		public const ushort SeqObjectHold = 0x000F;
		private const float SitBlend = 2.6153848f; // 0x40276277 - the sit animation's blend/duration (raw-body BE float)
		private const string SitAnimName = "chair_sit_calm";
		private const string StandAnimName = "standup";
		private const uint BenchSitAction = 6; // the bench's startobjectaction sub-action (turnon_execute)
		private const uint SitAnimAction = 42052;

		// The caster's component-ADD gesture on each weave add - a begin/complete pair carrying the added element
		// (frames 1657/1658): behaviorId 4, args {f1=AddAction, f3=5, f5=8, f6=0, f10=SpellTarget{caster}},
		// trailer [1][element][0x01][0][0][0][completeFlag].
		private const uint SpellweaveAddAction = 186463;

		// The caster's trance-EXIT animation on spellweave cancel/fire (cancel-weave capture, frame 141.61): behaviorId
		// 1, args {f1=ExitAction, f3=6, f5=8, f6=0, f11=79}, trailer [4][0x27][0][0][0]. Without it the buffs clear but
		// the character stays stuck in the trance pose. Action params are capture-derived (necro).
		private const uint SpellweaveExitAction = 41302;

		// The caster's TRANCE-POSE animation on spellweave ACTIVATE (tools/capture/decode_spellweave.py frame 1645):
		// behaviorId 1, args {f1=TranceAction, f3=4, f5=8, f6=0, f11=1199}, trailer [0x27][1][0][0][0]. Without it the
		// trance buffs render but the character stays idle. Action params are capture-derived (necro).
		private const uint SpellweaveTranceAction = 42041;
		private const uint StandAnimAction = 42053;

		// The object-action STATE record (behaviorId 0x12) that HOLDS the player in the seated pose. The sit ANIMATION
		// (PlayerSitOnObject) alone plays the sit-down transition then reverts to idle; retail brackets it with this
		// state record (F2793 seq=SeqBegin BEFORE the animation, F2801 seq=SeqObjectHold AFTER) so the client commits to
		// "using" the bench and stays seated until standup. f10 targets the PLAYER; the tail = [objectClass][objectHandle]
		// [action]. Byte-exact vs F2793 / F2801.
		public static byte[] BenchSitState(uint actor, ushort seq, uint benchHandle)
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
			var tail = new FaolanStream().WriteUInt32(EntityClass.SimpleDynel).WriteUInt32(benchHandle).WriteUInt32(BenchSitAction).ToArray();
			return Build(actor, seq, ObjectStateBehaviorId, args, tail);
		}

		// Sit the actor on a world object (a bench): plays "chair_sit_calm" aimed at the object. `remoteformat` is the
		// object-action feedback descriptor (SitRemoteformat, or a captured literal in tests). Byte-exact vs F2795.
		public static byte[] PlayerSitOnObject(uint actor, uint targetHandle, string remoteformat)
		{
			return ObjectActionAnimation(actor, SitAnimAction, 10, 0, EntityClass.SimpleDynel, targetHandle, SitAnimName, 2, 0x86, SitBlend, remoteformat, 0x4C);
		}

		// Stand the actor back up. Sent in answer to the CLIENT's standup request (behaviour 1, phase 2) - not inferred
		// from movement, which is what we used to do; see the behaviour-1 branch in GameServerListener.Inbound.
		// Byte-exact vs F2840.
		public static byte[] PlayerStandUp(uint actor, uint targetHandle, string remoteformat)
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

		public static byte[] SpellweaveComponentAdd(uint caster, uint elementBaseId, bool complete)
		{
			var message = ArgsPrefix(SpellweaveAddAction, 5, 0);
			message.Target = Target(EntityClass.Character, caster);
			var args = message.ToProtobuf();
			var trailer = new FaolanStream().WriteUInt32(1).WriteUInt32(elementBaseId).WriteByte(0x01).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).WriteByte(complete).ToArray();

			return Build(caster, complete
				? SeqComplete
				: SeqBegin, 4, args, trailer);
		}

		public static byte[] SpellweaveTrance(uint caster)
		{
			var message = ArgsPrefix(SpellweaveTranceAction, 4, 0);
			message.RechargeTime = 1199;
			var args = message.ToProtobuf();
			var trailer = new FaolanStream().WriteUInt32(0x27).WriteUInt32(1).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).ToArray();

			return Build(caster, SeqBegin, 1, args, trailer);
		}

		public static byte[] SpellweaveTranceExit(uint caster)
		{
			var message = ArgsPrefix(SpellweaveExitAction, 6, 0);
			message.RechargeTime = 79;
			var args = message.ToProtobuf();
			var trailer = new FaolanStream().WriteUInt32(4).WriteUInt32(0x27).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).ToArray();

			return Build(caster, SeqBegin, 1, args, trailer);
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

		// The shared BCC framing primitive: header + behaviorId + length-prefixed args + the behavior-specific trailer.
		private static byte[] Build(uint actor, ushort seq, uint behaviorId, byte[] args, byte[] trailer)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.BehaviorControlCenterIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(actor)
				.WriteUInt16(seq)
				.WriteUInt32(behaviorId)
				.WriteArrayUInt32Length(args)
				.WriteArray(trailer)
				.ToArray();
		}

		// A world-object action animation: the actor plays `animName` aimed at a target object, carrying a feedback
		// remoteformat. tail = [u32 a][u16 nameLen][animName][u32 b][f32 blend][5×00][u8 flag][remoteformat][u32 trailer].
		private static byte[] ObjectActionAnimation(uint actor, uint animActionId, uint p3, uint p6, uint targetClass, uint targetHandle, string animName, uint tailA, uint tailB, float blend,
			string remoteformat, uint trailer)
		{
			var message = ArgsPrefix(animActionId, p3, p6);
			message.Target = Target(targetClass, targetHandle);
			var args = message.ToProtobuf();
			var tail = new FaolanStream()
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
				.WriteUInt32(trailer)
				.ToArray();

			return Build(actor, SeqBegin, ObjectActionBehaviorId, args, tail);
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
