using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     The server-initiated <see cref="BehaviorControlCenterIIR" /> forms, as value factories.
	/// </summary>
	/// <remarks>
	///     Not a codec: nothing here encodes or decodes. Each method names one behaviour and returns the record for it, and
	///     <see cref="BehaviorControlCenterCodec" />.Build turns it into bytes. They are factories rather than Build
	///     overloads because the forms share parameter lists - <c>(uint actor, uint targetHandle, string remoteformat)</c>
	///     describes both the sit and the standup - so an overload could not tell them apart.
	///     <para>
	///         Every animation's args share one protobuf shape: {f1 = animActionId, f3 = phase, f5 = 8, f6, then either an
	///         f10 target sub-message or an f11 scalar}. The animActionId indexes the client's animation-action table
	///         (spellweave trance 42041, chair-sit 42052 and standup 42053 all sit in the same run). The tail is
	///         behaviour-specific.
	///     </para>
	/// </remarks>
	public static class BehaviorAnimations
	{
		// World-object action animations - the actor plays a named animation aimed at a target object, with a feedback
		// remoteformat (tools/capture/decode_sit.py, path-of-azura sit-bench capture). BehaviorId.ObjectInteraction,
		// seq begin; the anim string and blend constants below are byte-exact against F2795 (sit) / F2840 (standup).
		// Sitting on a bench is startobjectaction(33)|turnon_execute(4).

		public const ushort SeqBegin = 0x0004;
		public const ushort SeqComplete = 0x000A;

		/// <summary>The seq the trailing hold-assert uses (F2801); the leading hold-echo uses SeqBegin (F2793).</summary>
		public const ushort SeqObjectHold = 0x000F;

		/// <summary>NPC animation acks are server-initiated, so the echoed sequence is constant, not a client seq.</summary>
		private const ushort NpcAttackAckSeq = 0x0004;

		private const uint IdleBehaviorAction = 41413; // the capture's `08 c5 c3 02` varint

		/// <summary>
		///     The search stance's activation word - 159 on both captured classes, so it is the record's constant rather than
		///     the per-character part (that is the action id; see StartingData.SearchStanceAction).
		/// </summary>
		private const uint SearchActivation = 159;

		/// <summary>Entering hide is the one stance whose flags word is not 8. Both captured classes send 10.</summary>
		private const uint SneakHideEnterFlags = 10;

		/// <summary>
		///     The generic cast animation, shared by resting and the Path of Asura recall: it says "this character is
		///     casting", not which spell. The spell is in the tail.
		/// </summary>
		private const uint RestCastAction = 177216;

		/// <summary>The Resting spell (3164147) and its cast time in centiseconds; the Spells row states 20,500 ms.</summary>
		private const uint RestingSpell = 0x3047F3;
		private const uint RestActivation = 2050;

		private const float SitBlend = 2.6153848f; // 0x40276277 - the sit animation's blend/duration (raw-body BE float)
		private const string SitAnimName = "chair_sit_calm";
		private const string StandAnimName = "standup";
		private const uint BenchSitAction = 6; // the bench's startobjectaction sub-action (turnon_execute)
		private const uint SitAnimAction = 42052;
		private const uint StandAnimAction = 42053;

		/// <summary>
		///     The achievement vista's object action. It reuses the chair-sit animation action id - the gesture itself is
		///     content (social_item_accept_2) and the action id only says which animation table slot plays it - and its blend
		///     is why the camera does not start at once: retail's StartClientSideScryScriptWithParams lands 5.04 s after this
		///     record, when the gesture ends.
		/// </summary>
		private const uint VistaAnimAction = SitAnimAction;
		private const string VistaAnimName = "social_item_accept_2";

		/// <summary>How long that gesture runs - the record's blend, and therefore how long the camera start waits.</summary>
		public const float VistaGestureSeconds = 5.0f;
		private const float VistaBlend = VistaGestureSeconds;

		/// <summary>
		///     The object class the vista remoteformat names - the client's own word for the mechanism; retail sends
		///     startobjectaction(7)|interaction(2)|GenericAchievementVista.
		/// </summary>
		private const string VistaObjectClass = "GenericAchievementVista";

		/// <summary>
		///     The caster's component-add gesture on each weave add - a begin/complete pair carrying the added element
		///     (frames 1657/1658): behaviorId 4, args {f1=AddAction, f3=5, f5=8, f6=0, f10=Target{caster}}, tail
		///     [1][element][0x01][0][0][0][completeFlag].
		/// </summary>
		private const uint SpellweaveAddAction = 186463;

		/// <summary>
		///     The caster's trance-exit animation on spellweave cancel/fire (cancel-weave capture, frame 141.61): behaviorId
		///     1, args {f1=ExitAction, f3=6, f5=8, f6=0, f11=79}, tail [4][0x27][0][0][0]. Without it the buffs clear but the
		///     character stays stuck in the trance pose. The action parameters are capture-derived (necromancer).
		/// </summary>
		private const uint SpellweaveExitAction = 41302;

		/// <summary>
		///     The caster's trance-pose animation on spellweave activate (tools/capture/decode_spellweave.py, frame 1645):
		///     behaviorId 1, args {f1=TranceAction, f3=4, f5=8, f6=0, f11=1199}, tail [0x27][1][0][0][0]. Without it the
		///     trance buffs render but the character stays idle. The action parameters are capture-derived (necromancer).
		/// </summary>
		private const uint SpellweaveTranceAction = 42041;

		/// <summary>
		///     The generic registration form: the header plus a <see cref="CommandMessage" /> naming the behaviour slot, its
		///     id and its phase, with an optional 20-byte "bound" tail. It is what the create-editor burst streams for every
		///     entity it brings into play, and what a scripted NPC pose (Casilda's captive/freed) rides.
		/// </summary>
		/// <remarks>
		///     protobuf-net omits an unset optional and emits fields in ascending number order, so the optional
		///     f2/f4/f11/f12 land where retail puts them. <paramref name="form" /> is the wire seq: 0x0004 for the common
		///     registration, while the rarer 0x0007 subtype adds protobuf field 4 (a varint between phase and field 5). The
		///     bound tail ([u32 boundA][u32 boundB][12 x 00]) is decoded in tools/capture/decode_behaviorcc.py.
		/// </remarks>
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

		/// <summary>
		///     The companion-locomotion registration retail installs on a freed escortee: the slot-9 behaviour that lets her
		///     walk, carrying the full 88-byte behaviour-state tail rather than the short bound form.
		/// </summary>
		/// <remarks>
		///     The short bound tail is not enough here - the escortee then slides through a standing animation however exact
		///     the movement packets are. Retail's record (Hoxxling_..._login_gameplay idx 2106, 124 bytes) carries:
		///     <code>
		///     [u32 0x12][u32 0] + five more zero words   28 bytes - the short bound tail is only its first 20
		///     6 x NullIdentity.Pair                      48 bytes - the doubled unset-identity sentinel
		///     [u32 1][u32 1][u32 1]                      12 bytes - the state triple the create-world companion form
		///                                                           also carries
		///     </code>
		///     It is deliberately not built through <see cref="ItemRegistration" />: that form binds a behaviour to an
		///     equipped item and wants a handle, a 4-char tag and two transforms, none of which this record has. Same length
		///     family, different record.
		/// </remarks>
		public static BehaviorControlCenterIIR CompanionLocomotion(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6)
		{
			var message = new CommandMessage
			{
				BccActionId = behaviorId,
				CommandqueueServerId = phase,
				Flags = field5,
				ExecutionStatus = field6
			};

			using var tail = new FaolanStream();
			tail.WriteUInt32(0x12).WriteArray(new byte[24]);
			for (var i = 0; i < 6; i++)
				tail.WriteUInt32(NullIdentity.High).WriteUInt32(NullIdentity.Low);

			tail.WriteUInt32(1).WriteUInt32(1).WriteUInt32(1);
			return new BehaviorControlCenterIIR(handle, SeqBegin, slot, message.ToProtobuf(), tail.ToArray());
		}

		/// <summary>
		///     The long (116-byte) registration: the same head as <see cref="Registration" /> plus an 88-byte behaviour-state
		///     tail that binds the behaviour to an equipped item. The three records in the WorldPrep stream each pair with one
		///     of that burst's DispatchAddItem equipment pieces - item handle, 4-char entity tag and transform all match.
		///     <code>
		///     [u32 index][u32 someId][u32 itemHandle][u32 itemHandle][u32 0][u32 1][char[4] tag]
		///     [byte[16] transform][byte[16] transform][8-byte ownerStamp][8-byte ownerStamp][12 zero bytes]
		///     </code>
		/// </summary>
		/// <remarks>
		///     The item handle and 4-char tag are repeated; the 16-byte transform is the item's pose packed as four
		///     big-endian u32 (the same x/y/z and rotation values the paired DispatchAddItem carries as fixed32 protobuf
		///     fields), also repeated. <c>ownerStamp</c> (0xB290805C 0x29E627CA) is the fixed creator/timestamp pair that
		///     recurs across the whole WorldPrep burst; its semantics are unpinned. The trailing three u32 are 0 in the
		///     WorldPrep equipment records and carry a small state triple (0, 1, 1) in the create-world cutscene's
		///     mount/companion long form. Decoded from WorldPrepBurst.bin by tools/capture/decode_create_iir_records.py.
		/// </remarks>
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

		/// <summary>
		///     The object-action state record (behaviorId 0x12) that holds the player in the seated pose.
		/// </summary>
		/// <remarks>
		///     The sit animation (<see cref="PlayerSitOnObject" />) on its own plays the sit-down transition and then reverts
		///     to idle. Retail brackets it with this state record - F2793 at SeqBegin before the animation, F2801 at
		///     <see cref="SeqObjectHold" /> after it - so the client commits to using the bench and stays seated until
		///     standup. Field 10 targets the player; the tail is [objectClass][objectHandle][action]. Byte-exact against
		///     F2793 / F2801.
		/// </remarks>
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

		/// <summary>
		///     The short-form "this entity is alive and idling" assert: <c>BehaviorId.BccAction</c> carrying the one fixed
		///     idle animation and nothing else. It starts a standing creature's idle animation instead of leaving it frozen on
		///     its spawn pose. Byte-exact against the mount capture (frame 105); <paramref name="tick" /> is the per-actor
		///     commandqueue_server_id, which steps by 1 (29,246 of 29,996 captured steps), so the args length grows as its
		///     varint widens.
		/// </summary>
		/// <remarks>
		///     Despite the name this is not periodic: over the corpus's 37,965 captured records (--report ambient-animation)
		///     across 7,969 actors, the records per actor are p50 1 and p90 2. Retail asserts it once as an entity comes into
		///     view and then leaves it alone. The record that recurs is <see cref="AmbientEmote" />, a different command with
		///     a different animation each time.
		/// </remarks>
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

		/// <summary>
		///     The ambient animation: a standing NPC plays a random one of its creature's idle behaviours - leaning on a wall,
		///     scratching, looking round - as a <c>BehaviorId.Emote</c> with no tail. It is the recurring ambient record retail
		///     sends: 25,778 captured over 2,909 actors, p50 3 records each (p90 15). Byte-exact against frame 90 of
		///     NecroL80_..._chat-annotated-3 (actor 0x000304ED, animation 42115, 3580cs).
		/// </summary>
		/// <remarks>
		///     <paramref name="durationCentiseconds" /> is the animation's own length and the client is told it:
		///     activation_time (field 12) is present on 25,562 of the 25,778 records, and 76 of the 118 animation ids state
		///     exactly one value for it. The duration belongs to the animation, which is why it is stored beside the animation
		///     id rather than decided per send.
		///     <para>
		///         The next ambient animation is scheduled for after this one finishes: over 22,696 consecutive pairs the gap
		///         divided by the previous record's activation_time has p10 exactly 1.0 and falls below 0.9 on 121 (0.5%). The
		///         creature then stands for a while - p10 0.1 s, p50 2.2 s, p90 19.0 s - and plays another.
		///     </para>
		/// </remarks>
		public static BehaviorControlCenterIIR AmbientEmote(uint actor, uint animationId, uint tick, uint durationCentiseconds)
		{
			var args = new CommandMessage
			{
				BccActionId = animationId,
				CommandqueueServerId = tick,
				Flags = 0x18,
				ExecutionStatus = 0,
				ActivationTime = durationCentiseconds
			}.ToProtobuf();

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.Emote, args, []);
		}

		/// <summary>
		///     The NPC counter-attack animation ack - see docs/subsystems/combat.md. Structurally the same record as the
		///     player's targeted-hit attack ack with the roles swapped: the top-level actor is the NPC, args.Target and the
		///     result-block target are the player it hit, and the args carry no Mode field (that one is player-only, and is
		///     the NPC-vs-player tell). Sending it makes the client play the NPC's swing animation and the player's hit-react.
		/// </summary>
		/// <remarks>
		///     The damage here is the floating combat number only; the authoritative HP move rides the world-tick StatUpdate,
		///     kept separate so it is not applied twice. Decoded in tools/capture/decode_combat_npc_ack.py (98/98 frames) and
		///     byte-exact against frame 6472 of the strickenbla capture. The animation parameters
		///     (typeId/swingCounter/f5/f11/f12) and the result codes (resultType/resultK1/resultK2) are not semantically
		///     decoded: the defaults are capture-typical values for the live send, and the goldens pin the exact captured ones.
		///     <para>
		///         <paramref name="comboId" /> is the Combos row this swing performs, 0 for a plain swing. Retail puts it in
		///         the same combo_id field a player's own combo swing uses - 427 of the 1,504 captured NPC melee acks carry one
		///         - and never a combo_attack_num beyond the implicit 0, because an NPC combo is a single action marker with no
		///         key sequence to step through (--report combo-swings).
		///     </para>
		/// </remarks>
		public static BehaviorControlCenterIIR NpcAttackAck(uint npcHandle, uint playerHandle, uint damage, uint typeId, uint swingCounter, uint f5 = 24, uint f11 = 64, uint f12 = 56,
			byte resultType = 0xCB, uint resultK1 = 1, uint resultK2 = 2, uint comboId = 0)
		{
			return NpcAttackAck(npcHandle, playerHandle, damage, typeId, swingCounter, NpcAttackAckSeq, BehaviorId.CombatMelee, f5, f11, f12, resultType, resultK1, resultK2, comboId);
		}

		/// <summary>
		///     The two phases of a ranged shot. A ranged NPC's swing is Combat:Ranged (0x10), not Combat:Melee: a different
		///     behaviour id, one extra trailing byte on the result block, and two records per shot rather than one - a begin
		///     at seq 0x0004 and a complete at seq 0x000A, which the corpus sends in near-equal numbers (212 / 196 over 424
		///     records) carrying the same swing counter, the same combo id and a byte-identical tail. Melee has no such pair:
		///     1,957 of its 2,117 records are seq 0x0004 and it sends no complete.
		/// </summary>
		/// <remarks>
		///     The complete may also carry anim_random (field 4). We omit it deliberately - it is on only 26 of the 196
		///     captured completes, so leaving it out sends a subset of retail rather than an invented value.
		/// </remarks>
		public const ushort RangedBeginSeq = 0x0004;
		public const ushort RangedCompleteSeq = 0x000A;

		/// <summary>
		///     The bcc_action_id a ranged NPC shot names, the ranged twin of melee's 0xBEEC. The corpus clusters it at
		///     77813-77817 with the same per-NPC jitter melee shows; 77817 is the modal value (146 of 424).
		/// </summary>
		public const uint NpcShotBehaviorType = 77817;

		public static BehaviorControlCenterIIR NpcAttackAck(uint npcHandle, uint playerHandle, uint damage, uint typeId, uint swingCounter, ushort sequence, BehaviorId behavior, uint f5 = 24,
			uint f11 = 64, uint f12 = 56, byte resultType = 0xCB, uint resultK1 = 1, uint resultK2 = 2, uint comboId = 0)
		{
			// args protobuf: {f1=TypeId, f3=swingCounter, f5, f6=0, f7=combo, f10=Target{f1=0xC350, f2=player}, f11, f12}.
			// No f2/Mode: that field is present only on player-initiated actions. CommandMessage field 10 is the target,
			// a ProtoID32 { type, instance } whose first word is an entity class.
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

			// The result block is BehaviorAcks': the player's targeted-hit ack sends the same 81 bytes with the roles
			// swapped. A ranged shot's block closes with one extra 0x00, the only difference between the two.
			var ranged = behavior == BehaviorId.CombatRanged;
			return new BehaviorControlCenterIIR(npcHandle, sequence, (uint)behavior, args, BehaviorAcks.HitResultBlock(playerHandle, damage, resultType, resultK1, resultK2, ranged));
		}

		/// <summary>
		///     Sits the actor on a world object (a bench): plays "chair_sit_calm" aimed at the object.
		///     <paramref name="remoteformat" /> is the object-action feedback descriptor (<see cref="SitRemoteformat" />, or a
		///     captured literal in tests). Byte-exact against F2795.
		/// </summary>
		public static BehaviorControlCenterIIR PlayerSitOnObject(uint actor, uint targetHandle, string remoteformat)
		{
			return ObjectActionAnimation(actor, SitAnimAction, 10, 0, EntityClass.SimpleDynel, targetHandle, SitAnimName, 2, 0x86, SitBlend, remoteformat, 0x4C);
		}

		/// <summary>
		///     Stands the actor back up, in answer to the client's standup request (behaviour 1, phase 2) rather than inferred
		///     from movement; see the behaviour-1 branch in GameServerListener.Inbound. Byte-exact against F2840.
		/// </summary>
		public static BehaviorControlCenterIIR PlayerStandUp(uint actor, uint targetHandle, string remoteformat)
		{
			return ObjectActionAnimation(actor, StandAnimAction, 12, 20, EntityClass.SimpleDynel, targetHandle, StandAnimName, 0, 0, 0f, remoteformat, 0x01);
		}

		/// <summary>
		///     The sit feedback remoteformat: token startobjectaction(33)|turnon_execute(4)|&lt;objectClass&gt;, category
		///     50026, and the object's localized action key. The key is empty when it has not been imported; it drives the
		///     subtitle lookup, not the animation.
		/// </summary>
		public static string SitRemoteformat(string objectClass, string key = "")
		{
			return $"<remoteformat token=\"startobjectaction(33)|turnon_execute(4)|{objectClass}\" category=\"50026\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}

		/// <summary>
		///     The achievement vista's object action: the player plays a five-second "accept" gesture at the viewpoint and the
		///     camera fly-through starts when it ends. The same record as the bench sit - the same startobjectaction mechanism
		///     - with the vista's own animation, phase and blend. Byte-exact against f4029.
		/// </summary>
		public static BehaviorControlCenterIIR PlayerViewVista(uint actor, uint targetHandle, string remoteformat)
		{
			return ObjectActionAnimation(actor, VistaAnimAction, 5, 0, EntityClass.SimpleDynel, targetHandle, VistaAnimName, 2, 0, VistaBlend, remoteformat, 0x01);
		}

		/// <summary>
		///     The vista feedback remoteformat. The client names the mechanism itself - GenericAchievementVista - so retail
		///     has a content pattern for viewpoints rather than one scripted object. <paramref name="actionIndex" /> is the
		///     object's own action slot and appears twice, in the token and as the record's one string parameter;
		///     <paramref name="key" /> is the localized action key, empty for the same reason as the sit's.
		/// </summary>
		public static string VistaRemoteformat(uint actionIndex, string key = "")
		{
			return $"<remoteformat token=\"startobjectaction({actionIndex})|interaction(2)|{VistaObjectClass}\" category=\"50026\" key=\"{key}\" knubot=\"0\"  >" +
				$"<parameter value=\"{actionIndex};\" name=\"String\" /></remoteformat>";
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

		/// <summary>
		///     The search stance, retail's answer to PerformSpecialAction 0x064: slot BehaviorId.Search, form 4, flags 8,
		///     activation 159. All of that is identical on both captured classes, which is why only
		///     <paramref name="animActionId" /> is a parameter (StartingData.SearchStanceAction). The five-byte tail is zero
		///     in every capture but one, where its first byte is 1; that byte is a parameter rather than a constant, since one
		///     differing sighting does not pin it. [necro f15625 / f2898, hox f3461]
		/// </summary>
		public static BehaviorControlCenterIIR SearchStance(uint actor, uint animActionId, uint phase, byte lead = 0)
		{
			var message = ArgsPrefix(animActionId, phase, 0);
			message.ActivationTime = SearchActivation;

			using var tail = new FaolanStream();
			tail.WriteByte(lead).WriteArray(new byte[4]);

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.Search, message.ToProtobuf(), tail.ToArray());
		}

		/// <summary>
		///     Entering hide, retail's answer to PerformSpecialAction 0x068: slot BehaviorId.SneakHide, form 4, and - unlike
		///     every other stance here - flags 10 rather than 8, with the client's own command id echoed back in
		///     CommandqueueClientId. The four-byte tail is the shadow coverage the client just reported in its
		///     SetShadowCoverageIIR: 100 in every observation, but a parameter rather than a constant because the client
		///     computes it from local lighting. [necro f830 / f15643, hox f3420]
		/// </summary>
		public static BehaviorControlCenterIIR SneakHideEnter(uint actor, uint animActionId, uint phase, uint clientCommandId, uint shadowCoverage)
		{
			var message = ArgsPrefix(animActionId, phase, 0);
			message.Flags = SneakHideEnterFlags;
			message.CommandqueueClientId = clientCommandId;

			using var tail = new FaolanStream();
			tail.WriteUInt32(shadowCoverage);

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.SneakHide, message.ToProtobuf(), tail.ToArray());
		}

		/// <summary>
		///     Leaving hide: the same slot as the enter, but back to flags 8 with no client command id and an eight-byte zero
		///     tail. The action id is not class-specific - both captured characters exit with
		///     StartingData.SneakHideExitAction - because the enter animation is the character's own sneak pose while the exit
		///     is the shared stand-up. [necro f843 / f15660, hox f3452]
		/// </summary>
		public static BehaviorControlCenterIIR SneakHideExit(uint actor, uint animActionId, uint phase)
		{
			var message = ArgsPrefix(animActionId, phase, 0);

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.SneakHide, message.ToProtobuf(), new byte[8]);
		}

		/// <summary>
		///     Resting, retail's answer to PerformSpecialAction 0x06A. It is not a stance record like search or hide: it is a
		///     self-cast of the spell the client calls Resting ("Recovering health, mana, energy, and stamina"), which is why
		///     it rides slot 4 MagicAttack with a spell target and a cast time.
		/// </summary>
		/// <remarks>
		///     RestCastAction (177216) is the generic cast animation, shared with the Path of Asura recall, so it is neither
		///     class- nor spell-specific; six sightings across five captures and four characters, necromancer and Herald of
		///     Xotli alike, all carry it. The activation word 2050 is the Resting spell row's own 20,500 ms cast time in
		///     centiseconds. The 22-byte tail is byte-identical in every sighting and carries the wire spell id
		///     (base &lt;&lt; 8 | rank).
		/// </remarks>
		public static BehaviorControlCenterIIR RestCast(uint actor, uint phase)
		{
			var message = ArgsPrefix(RestCastAction, phase, 0);
			message.Target = Target(EntityClass.Character, actor);
			message.ActivationTime = RestActivation;

			using var tail = new FaolanStream();
			tail.WriteUInt32(1).WriteUInt32(RestingSpell).WriteByte(0x01).WriteArray(new byte[10]).WriteByte(0x08).WriteByte(0x02).WriteByte(0x00);

			return new BehaviorControlCenterIIR(actor, SeqBegin, 4, message.ToProtobuf(), tail.ToArray());
		}

		/// <summary>The standup feedback remoteformat: shows "Standing up" as the on-screen feedback.</summary>
		public static string StandRemoteformat()
		{
			return "<remoteformat id=\"0\" category=\"0\" key=\"\" knubot=\"0\"  ><parameter value=\"7;Standing up\" /></remoteformat>";
		}

		/// <summary>
		///     The args every animation shares: a CommandMessage {bcc_action_id, commandqueue_server_id, flags=8,
		///     execution_status}. Callers set Target (field 10) or RechargeTime (field 11) before serializing.
		/// </summary>
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

		/// <summary>
		///     A world-object action animation: the actor plays <paramref name="animName" /> aimed at a target object,
		///     carrying a feedback remoteformat. The tail is
		///     [u32 a][u16 nameLen][animName][u32 b][f32 blend][4x00][u16 rfLen][remoteformat][u32 trailer].
		/// </summary>
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
				// The remoteformat is a length-prefixed string, exactly like animName above, and the prefix must be
				// computed from the string actually sent: a generated remoteformat differs in length from the captured
				// one (we emit key="" until the object's localized action key is imported), and a prefix that overstates
				// the string makes the client read past the end and drop the whole record ("Figos ID 0 sent a too short
				// IIR UNKNOWN Key) ... datasize left in stream=141"). The two captured records pin the encoding: the
				// sit's prefix is 0xAA (170) for a 170-byte string, the standup's 0x84 (132) for a 132-byte one.
				.WriteString(remoteformat)
				.WriteUInt32(trailer);

			return new BehaviorControlCenterIIR(actor, SeqBegin, (uint)BehaviorId.ObjectInteraction, message.ToProtobuf(), tail.ToArray());
		}

		/// <summary>Field 10 - who or what the animation is aimed at, as a ProtoID32 { type, instance }.</summary>
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
