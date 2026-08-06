using System;
using System.Text;
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
		// remoteformat (decode_sit.py, path-of-azura sit-bench capture). BehaviorId.ObjectInteraction, seq begin; the
		// anim string + blend constants below are byte-exact vs F2795 (sit) / F2840 (standup). animActionId sits beside
		// the spellweave actions in the same table (42052/42053). Sitting on a bench is
		// startobjectaction(33)|turnon_execute(4).

		public const ushort SeqBegin = 0x0004;
		public const ushort SeqComplete = 0x000A;

		// The seq the trailing hold-assert uses (F2801); the leading hold-echo uses SeqBegin (F2793).
		public const ushort SeqObjectHold = 0x000F;

		// NPC animation acks are server-initiated, so the echoed sequence is the constant 0x0004 (not a client seq).
		private const ushort NpcAttackAckSeq = 0x0004;

		private const uint IdleBehaviorAction = 41413; // the capture's `08 c5 c3 02` varint

		// The search stance's `activation` word - 159 on both captured classes, so it is the record's constant and not
		// the per-character part (that is the action id; see StartingData.SearchStanceAction).
		private const uint SearchActivation = 159;

		// Entering hide is the one stance whose `flags` is not 8. Both captured classes send 10.
		private const uint SneakHideEnterFlags = 10;

		// The GENERIC cast animation - shared by resting and the Path of Asura recall, so it says "this character is
		// casting", not which spell. The spell is in the tail.
		private const uint RestCastAction = 177216;

		// The `Resting` spell (3164147), and its cast time in centiseconds - the Spells row says 20,500 ms.
		private const uint RestingSpell = 0x3047F3;
		private const uint RestActivation = 2050;

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

		// The LONG (116-byte) registration: the same head as `Registration` plus an 88-byte behaviour-state tail that
		// binds the behaviour to an EQUIPPED ITEM (the three records in the WorldPrep stream each pair with one of that
		// burst's DispatchAddItem equipment pieces - the item's handle, its 4-char entity tag and its transform all
		// match):
		//   [u32 index][u32 someId][u32 itemHandle][u32 itemHandle][u32 0][u32 1][char[4] tag]
		//   [byte[16] transform][byte[16] transform][8-byte ownerStamp][8-byte ownerStamp][12 zero bytes]
		// The item handle + 4-char tag are repeated; the 16-byte transform is the item's pose packed as four
		// big-endian u32 (the same x/y/z + rotation values the paired DispatchAddItem carries as fixed32 protobuf
		// fields), also repeated; `ownerStamp` (0xB290805C 0x29E627CA) is the fixed creator/timestamp pair that
		// recurs across the whole WorldPrep burst (semantics unpinned). The trailing 3 u32 are 0 in the WorldPrep
		// equipment records but carry a small state pair (e.g. 0,1,1) in the create-world cutscene's mount/companion
		// long form. Decoded from WorldPrepBurst.bin by tools/capture/decode_create_iir_records.py.
		public static BehaviorControlCenterIIR ItemRegistration(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6, uint index, uint someId, uint itemHandle, string tag,
			byte[] transform, uint trailingA = 0, uint trailingB = 0, uint trailingC = 0)
		{
			var tagBytes = Encoding.Latin1.GetBytes(tag);
			if (tagBytes.Length != 4)
				throw new ArgumentException("behavior entity tag must be 4 ASCII chars", nameof(tag));
			if (transform.Length != 16)
				throw new ArgumentException("behavior transform must be 16 bytes", nameof(transform));

			using var itemTail = new FaolanStream();
			itemTail
				.WriteUInt32(index)
				.WriteUInt32(someId)
				.WriteUInt32(itemHandle)
				.WriteUInt32(itemHandle)
				.WriteUInt32(0u)
				.WriteUInt32(1u)
				.WriteArray(tagBytes)
				.WriteArray(transform)
				.WriteArray(transform)
				.WriteArray(NullIdentity.Bytes)
				.WriteArray(NullIdentity.Bytes)
				.WriteUInt32(trailingA)
				.WriteUInt32(trailingB)
				.WriteUInt32(trailingC);

			return Registration(handle, slot, behaviorId, phase, field5, field6) with { Tail = itemTail.ToArray() };
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
			return new BehaviorControlCenterIIR(actor, seq, (uint)BehaviorId.UseDynel, args, tail.ToArray());
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

			return new BehaviorControlCenterIIR(actor, NpcAttackAckSeq, (uint)BehaviorId.BccAction, args, []);
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
		//
		// `comboId` (#623) is the `Combos` row this swing performs, 0 for a plain one. Retail puts it in the SAME
		// `combo_id` field a player's own combo swing uses - 427 of the 1,504 captured NPC melee acks carry one -
		// and never a `combo_attack_num` beyond the implicit 0, because an NPC combo is a single `action` marker
		// with no key sequence to step through (`--report combo-swings`).
		public static BehaviorControlCenterIIR NpcAttackAck(uint npcHandle, uint playerHandle, uint damage, uint typeId, uint swingCounter, uint f5 = 24, uint f11 = 64, uint f12 = 56,
			byte resultType = 0xCB, uint resultK1 = 1, uint resultK2 = 2, uint comboId = 0)
		{
			// args protobuf: {f1=TypeId, f3=swingCounter, f5, f6=0, f7=combo, f10=Target{f1=0xC350, f2=player}, f11, f12}.
			// NO f2/Mode - that field is present only on player-initiated actions and is the NPC-vs-player tell.
			// CommandMessage field 10 is the TARGET - a ProtoID32 { type, instance }, not the "spell target" the old
			// hand-reconstructed schema called it: its first word is an entity class (Gitea #387).
			var message = new CommandMessage
			{
				BccActionId = typeId,
				CommandqueueServerId = swingCounter,
				Flags = f5,
				ExecutionStatus = 0,
				Target = Target(EntityClass.Character, playerHandle),
				RechargeTime = f11,
				ActivationTime = f12
			};
			if (comboId != 0)
				message.ComboId = comboId;

			var args = message.ToProtobuf();

			// The result block is BehaviorAcks' - the player's targeted-hit ack sends the same 81 bytes with the roles
			// swapped, and it was written out in both files until #530 wave 7.
			return new BehaviorControlCenterIIR(npcHandle, NpcAttackAckSeq, (uint)BehaviorId.CombatMelee, args, BehaviorAcks.HitResultBlock(playerHandle, damage, resultType, resultK1, resultK2));
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

		// The SEARCH stance (#505), retail's answer to `PerformSpecialAction` 0x064. Slot `BehaviorId.Search`, form 4,
		// flags 8, `activation` 159 - identical on both captured classes, which is why only `animActionId` is a
		// parameter (`StartingData.SearchStanceAction`). The five-byte tail is zero in every capture but one, where
		// its FIRST byte is 1; that byte is passed rather than pinned, since one differing sighting is not a constant.
		// [necro f15625 / f2898, hox f3461]
		public static BehaviorControlCenterIIR SearchStance(uint actor, uint animActionId, uint phase, byte lead = 0)
		{
			var message = ArgsPrefix(animActionId, phase, 0);
			message.ActivationTime = SearchActivation;

			using var tail = new FaolanStream();
			tail.WriteByte(lead).WriteArray(new byte[4]);

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.Search, message.ToProtobuf(), tail.ToArray());
		}

		// ENTERING hide (#505), retail's answer to `PerformSpecialAction` 0x068. Slot `BehaviorId.SneakHide`, form 4,
		// and - unlike every other stance here - `flags` 10 rather than 8, with the client's own command id echoed
		// back in `CommandqueueClientId`. The four-byte tail is the SHADOW COVERAGE the client just reported in its
		// SetShadowCoverageIIR (100 in every observation, hence a parameter rather than a constant: the client
		// computes it from local lighting, which is the whole reason it is the one that sends it).
		// [necro f830 / f15643, hox f3420]
		public static BehaviorControlCenterIIR SneakHideEnter(uint actor, uint animActionId, uint phase, uint clientCommandId, uint shadowCoverage)
		{
			var message = ArgsPrefix(animActionId, phase, 0);
			message.Flags = SneakHideEnterFlags;
			message.CommandqueueClientId = clientCommandId;

			using var tail = new FaolanStream();
			tail.WriteUInt32(shadowCoverage);

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.SneakHide, message.ToProtobuf(), tail.ToArray());
		}

		// LEAVING hide (#505). Same slot as the enter, but back to `flags` 8 with no client command id, an eight-byte
		// zero tail, and - the part worth noticing - an action id that is NOT class-specific: both captured characters
		// exit with `StartingData.SneakHideExitAction`. The enter animation is the character's own sneak pose; the
		// exit is the shared stand-up. [necro f843 / f15660, hox f3452]
		public static BehaviorControlCenterIIR SneakHideExit(uint actor, uint animActionId, uint phase)
		{
			var message = ArgsPrefix(animActionId, phase, 0);

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.SneakHide, message.ToProtobuf(), new byte[8]);
		}

		// RESTING (#505), retail's answer to `PerformSpecialAction` 0x06A. It is not a stance record like search or
		// hide at all - it is a self-CAST of the spell the client calls `Resting` ("Recovering health, mana, energy,
		// and stamina"), which is why it rides slot 4 `MagicAttack` with a spell target and a cast time.
		//
		// Two things make this the most solidly evidenced action on the page, not the least:
		//   - `RestCastAction` (177216) is the GENERIC cast animation, shared with the Path of Asura recall, so it is
		//     neither class- nor spell-specific. Six sightings across five captures and four characters, necromancer
		//     and Herald of Xotli alike, all carry it.
		//   - `activation` 2050 is not a magic number: it is the `Resting` spell row's own 20,500 ms cast time in
		//     centiseconds, which is what the whole args block is saying - "this character is casting Resting".
		// The 22-byte tail is byte-identical in every sighting and carries the wire spell id (base << 8 | rank).
		public static BehaviorControlCenterIIR RestCast(uint actor, uint phase)
		{
			var message = ArgsPrefix(RestCastAction, phase, 0);
			message.Target = Target(EntityClass.Character, actor);
			message.ActivationTime = RestActivation;

			using var tail = new FaolanStream();
			tail.WriteUInt32(1).WriteUInt32(RestingSpell).WriteByte(0x01).WriteArray(new byte[10]).WriteByte(0x08).WriteByte(0x02).WriteByte(0x00);

			return new BehaviorControlCenterIIR(actor, SeqBegin, 4, message.ToProtobuf(), tail.ToArray());
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

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.ObjectInteraction, message.ToProtobuf(), tail.ToArray());
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
