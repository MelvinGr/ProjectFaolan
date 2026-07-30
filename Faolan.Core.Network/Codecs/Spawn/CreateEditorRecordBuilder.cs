using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Constructive builders for the fixed-grammar IIR records the character-create EDITOR streams on its
	// GameAgent->GameInterface 0x2000 conversation (issue #189). Decoded by tools/capture/decode_create_editor.py and
	// validated byte-exact against every create capture (docs/subsystems/create-editor-bursts.md). These are the small,
	// fully-decoded records; the big entity full-updates (SimpleChar/SimpleDynel) reuse the existing spawn
	// builders (ProtoVec3/ProtoQuat transform + the recovered embedded protobufs) rather than a bespoke decoder.
	//
	// Each returns just the IIR RECORD (the bytes after the 0x2000 carrier's [u32 len][u32 iirType] prefix); the
	// caller frames it with WriteArrayUInt32Length(s => s.WriteUInt32(iirType).WriteArray(record)) as the existing
	// RetailCreateEditor*Burst Iir() helper already does.
	public static class CreateEditorRecordBuilder
	{
		// The create-editor IIR types this builder is the CANONICAL emitter of (the ones without a dedicated codec).
		// The other Build* methods below (GameTime, PlayfieldFiir, Regen, Feedback, ComboBook, ...) build bodies for
		// types owned by their own codecs (GameTimeCodec, MapChangeCodec, ...), which declare those - so they are not
		// listed here (declaring twice would double-attribute in the coverage matrix). #317 convention.
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.CharacterActionIIR, GameServer0X2000Opcodes.CharInPlayIIR, GameServer0X2000Opcodes.SetMorphValueIIR, GameServer0X2000Opcodes.SetNameIIR
		];

		// The entity-class tag every per-character editor record carries (EntityClass.Character = 0xC350).

		// BehaviorControlCenterIIR (0x642CD3D6), LONG form (116 bytes): the same 28-byte head as the short form
		// below, plus an 88-byte behavior-state tail that binds the behavior to an EQUIPPED ITEM (the three records
		// in WorldPrep each pair with one of the burst's DispatchAddItem equipment pieces - the item's handle, its
		// 4-char entity tag and its transform all match):
		//   <head: short-form bytes, slot 9, msgLen 10, protobuf 08 <behaviorId> 18 <phase> 28 <field5> 30 <field6>>
		//   [u32 index][u32 someId][u32 itemHandle][u32 itemHandle][u32 0][u32 1][char[4] tag]
		//   [byte[16] transform][byte[16] transform][8-byte ownerStamp][8-byte ownerStamp][12 zero bytes]
		// The item handle + 4-char tag are repeated; the 16-byte transform is the item's pose packed as four
		// big-endian u32 (the same x/y/z + rotation values the paired DispatchAddItem carries as fixed32 protobuf
		// fields), also repeated; `ownerStamp` (0xB290805C 0x29E627CA) is the fixed creator/timestamp pair that
		// recurs across the whole WorldPrep burst (semantics unpinned). Decoded from WorldPrepBurst.bin by
		// tools/capture/decode_create_iir_records.py and locked byte-exact by CreateEditorRecordTests.
		// BehaviorControlCenterIIR (0x642CD3D6), registration form: registers one behavior on an entity.
		//   [u32 type][u32 handle][u16 0x0004][u32 slot][u32 msgLen][protobuf][optional 20-byte bound tail]
		// protobuf = base varint fields `08 <behaviorId> [10 <field2>] 18 <phase> 28 <field5> 30 <field6>
		// [58 <field11>] [60 <field12>]`, msgLen = its length. The optional fields (2/11/12) and the 20-byte
		// "bound" tail (`[u32 boundA][u32 boundB][12x 00]`) were decoded from the create-world cutscene burst
		// (tools/capture/decode_behaviorcc.py, #234): of its 90 records the field set is {1,3,5,6} with optional
		// {2,11,12} and a 0- or 20-byte tail. (A separate 88-byte item-bound long form has its own builder,
		// BuildBehaviorControlCenterIirLong below.) Decoded by tools/capture/decode_create_iir_records.py + decode_behaviorcc.py.
		// `form` is 0x0004 for the common registration; the rarer 0x0007 subtype adds protobuf field 4 (a varint,
		// between phase and field5). Protobuf wire order: f1=behaviorId, [f2], f3=phase, [f4], f5, f6, [f11], [f12].
		public static byte[] BuildBehaviorControlCenterIir(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6, uint type = EntityClass.Character, uint? field2 = null,
			uint? field4 = null, uint? field11 = null, uint? field12 = null, uint? boundA = null, uint? boundB = null, ushort form = 0x0004)
		{
			// protobuf-net omits an unset optional and emits fields in ascending order, so the optional f2/f4/f11/f12
			// land exactly where the hand-written writer put them.
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

			var s = new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt16(form).WriteUInt32(slot).WriteArrayUInt32Length(message.ToProtobuf());
			if (boundA is { } a)
				s.WriteUInt32(a).WriteUInt32(boundB ?? 0u).WriteArray(new byte[12]);
			return s.ToArray();
		}

		// The trailing 3 u32 (`trailingA/B/C`) are 0 in the WorldPrep equipment records but carry a small state pair
		// (e.g. 0,1,1) in the create-world cutscene's mount/companion long form - so they're parameterized (default 0).
		public static byte[] BuildBehaviorControlCenterIirLong(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6, uint index, uint someId, uint itemHandle, string tag,
			byte[] transform, uint type = EntityClass.Character, uint trailingA = 0, uint trailingB = 0, uint trailingC = 0)
		{
			var tagBytes = Encoding.Latin1.GetBytes(tag);
			if (tagBytes.Length != 4)
				throw new ArgumentException("behavior entity tag must be 4 ASCII chars", nameof(tag));
			if (transform.Length != 16)
				throw new ArgumentException("behavior transform must be 16 bytes", nameof(transform));

			return new FaolanStream()
				.WriteArray(BuildBehaviorControlCenterIir(handle, slot, behaviorId, phase, field5, field6, type))
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
				.WriteUInt32(trailingC)
				.ToArray();
		}

		// CharInPlayIIR (0x864CFEF8): the "character is now in play" signal. 9 bytes:
		//   [u32 type][u32 handle][u8 flag]
		// An EMPTY record - the client reader (FUN_0075a920) only resolves the SimpleChar for `handle` and logs; the
		// carrier handle is the whole message (docs/re/iir-readers-combat-world.md). flag is the base IIR's, 0 in every capture.
		public static byte[] BuildCharInPlayIir(uint handle, byte flag = 0, uint type = EntityClass.Character)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).ToArray();
		}

		// CharacterActionIIR (0x6B6FD368): grants one action/ability id to an entity. 41 bytes:
		//   [u32 type][u32 handle][u32 0][u8 0x1D][16x 0x00][u32 actionId][8x 0x00]
		// The create-editor WorldPrep burst streams one per granted id (the same ability/feat id space as the
		// player-spawn scaffold's id list - e.g. 0x050E24). Decoded by tools/capture/decode_character_action.py and
		// validated byte-exact against every record in the WorldPrep capture (CreateEditorRecordTests).
		public static byte[] BuildCharacterActionIir(uint handle, uint actionId, uint type = EntityClass.Character)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt32(0u).WriteByte(0x1D).WriteArray(new byte[16]).WriteUInt32(actionId).WriteArray(new byte[8]).ToArray();
		}

		// ComboBookIIR (0xA8BBEB7F): grants one combo/ability to the entity's combo book. 14 bytes:
		//   [u32 type][u32 handle][u16 0x0001][u32 comboId]
		// The WorldPrep burst streams one per combo id. Decoded by tools/capture/decode_create_iir_records.py.
		public static byte[] BuildComboBookIir(uint handle, uint comboId, uint type = EntityClass.Character)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt16(0x0001).WriteUInt32(comboId).ToArray();
		}

		// EnergyIIR (0x40749B8A): an entity energy/value record. 13 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 value]
		public static byte[] BuildEnergyIir(uint type, uint handle, byte flag, uint value)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).WriteUInt32(value).ToArray();
		}

		// FeedbackIIR (0xBA1FD222): a UI feedback signal. 22 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 a][u32 b][u32 c][u8 d]
		// The client reader (FUN_0079f8e0) reads [u32x3][u8] after the base IIR flag (docs/re/iir-readers-conversation-misc.md);
		// the three words + trailing byte are an opaque numeric feedback payload, carried by value.
		public static byte[] BuildFeedbackIir(uint handle, byte flag, uint a, uint b, uint c, byte d, uint type = EntityClass.Character)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).WriteUInt32(a).WriteUInt32(b).WriteUInt32(c).WriteByte(d).ToArray();
		}

		// GameTimeIIR (0x4F57DC08): the world clock - time of day, the resync anchor, and the clock rate. 29 bytes:
		//
		//   [u32 0][u32 0][u8 isToBePassed][f32 dayTimeSeconds][u32 word][u32 gameDay][u32 unixSeconds][f32 rate]
		//
		// The field split is CLIENT-CONFIRMED, and it is one byte off from what this used to claim (#351). The old
		// reading - `[u64 dayTime][u8 flag]` - happens to produce the same bytes for a captured value, so the golden
		// test could not tell the two apart. The client can: `GameTimeIIR_t::ReadSubClass` reads a BOOL and then five
		// u32s, so sending a dayTime whose top byte is not 0/1 is rejected outright -
		//   "ReadSubClass failed for IIR 1331158024 (GameTimeIIR_t)! isToBePassed on bool has value G"
		// - 'G' being 0x47, the leading byte of a float we had put where the bool belongs.
		//
		// So `dayTime` is an IEEE float of GAME SECONDS, not a 32.32 fixed-point integer: the captured 0x472BDD00 is
		// 43997.0s = 12:13, which is what the client renders (verified in-client). Read as an integer the same word
		// would be 1.19e9 seconds, and the value we used to send - a plain integer second count - landed in the float's
		// exponent field as ~1e-38 and rendered as midnight.
		//
		// `word` is read into the IIR but never forwarded to the clock by the apply; it holds 0..3 across the corpus.
		// `gameDay` + `unixSeconds` are the two halves of the resync anchor (see GameTimeCodec.ResyncTimestamp), and
		// the rate is the day-cycle speed (1.0 in every capture). Raw-body floats are big-endian.
		public static byte[] BuildGameTimeIir(float dayTimeSeconds, uint word, uint gameDay, uint unixSeconds, bool isToBePassed = true, float rate = 1.0f)
		{
			return new FaolanStream()
				.WriteUInt32(0u)
				.WriteUInt32(0u)
				.WriteByte(isToBePassed)
				.WriteFloat(dayTimeSeconds)
				.WriteUInt32(word)
				.WriteUInt32(gameDay)
				.WriteUInt32(unixSeconds)
				.WriteFloat(rate)
				.ToArray();
		}

		// PlayfieldFIIR (0x5D85BFC7, "MapChange"): announces the playfield the client is entering - its reference
		// position (a Vec3, raw-body big-endian floats), the map id, the entering character instance, and the live
		// per-session playfield token (carried TWICE). 103 bytes.
		//
		// Framed the way the CLIENT reads it (#451). Its reader is a two-part chain: PlayfieldFIIR_c's own
		// ReadSubClass is FUN_008f36e0, which calls the base FUN_013d0a20 and then reads the four trailing words:
		//
		//   [u32 c350][u32 handle][u8 env]     the IIR base carrier                            @0..8
		//   [f32 X][f32 Y][f32 Z]              -> obj+0x24 / +0x28 / +0x2C                     @9..20
		//   [PlayfieldProxy]                   the FUN_00a6a600 stream, EMBEDDED here          @21..54
		//   [u32 sceneWord]                    -> obj+0x54                                     @55..58
		//   [u32 len = 24][24-byte blob]       the FUN_00cbf5d0 length-prefixed sub-stream      @59..86
		//   [u32][u32][u32 fieldC][u32 flags]  -> obj+0x80 / +0x84 / +0x88 / +0x8C             @87..102
		//
		// Validated over all 151 captured PlayfieldFIIR bodies: every one is 103 bytes, carries the proxy's 'b'
		// version byte at @21, and its blob length word reads exactly 24 so that 59 + 4 + 24 + 16 == 103. Zero
		// failures.
		//
		// That framing is one byte off from how this builder used to write the middle, which is why `sceneWord` used
		// to be a `field58 = 0x01000000` - a u32 whose only meaningful byte was its TOP one, because the proxy's
		// trailing bool had been absorbed into a leading zero word. Read aligned it is a plain 0 or 1 (54 / 97), and
		// `fieldB` turns out to be the LAST WORD OF THE BLOB rather than a top-level field. Neither is semantically
		// pinned - `sceneWord` is 0 on maps 4020/4022 and 1 on 1000/1010/2000/2010/2040/3000/5007/11040 while 4000
		// and 4010 carry both, and `blobLastWord` takes 0/1/2/3 within a single map - so both are still passed
		// through. [V0 on their meaning; V1 on the framing.]
		//
		// `nearClip` = 15.0 and `farClip` (~12-14) are likewise carried. Constructing this drives the map change from
		// the Map row + the session token instead of replaying the captured MapChange.
		public static byte[] BuildPlayfieldFiir(float posX, float posY, float posZ, uint mapId, uint charInstance, uint count, uint playfieldToken, float nearClip, float farClip, uint flags,
			uint sceneWord = 0u, uint blobLastWord = 0u, uint fieldA = 0u, uint fieldC = 7u)
		{
			// The length-prefixed sub-blob at @59: the client reads it through FUN_00cbf5d0, so its contents are a
			// nested stream and not four more top-level words. 24 bytes in all 151 captured records.
			var sceneBlob = new FaolanStream()
				.WriteUInt32(0x00000001u)
				.WriteUInt32(playfieldToken) // the token again, inside the blob
				.WriteUInt32(0x0000CB20u)
				.WriteFloat(nearClip)
				.WriteFloat(farClip)
				.WriteUInt32(blobLastWord) // beach capture carries 0x3, retail Old Tarantia 0x0
				.ToArray();

			var s = new FaolanStream()
				.WriteUInt32(0u)
				.WriteUInt32(0u)
				.WriteByte(0x00) // [0:9] the IIR base carrier: [u32 c350][u32 handle][u8 env]
				.WriteFloat(posX)
				.WriteFloat(posY)
				.WriteFloat(posZ); // [9:21] reference position

			// [21:55] the embedded PlayfieldProxy. Slot [2] is the per-map fieldA and slot [5] the small count -
			// two slots the teleport and team-member embeddings both read as constants; see PlayfieldProxy.
			new PlayfieldProxy(mapId, PlayfieldProxy.PlayfieldClass, charInstance, count, PlayfieldProxy.PinnedInstanceWord, playfieldToken, OwnerType: fieldA).Write(s);

			return s
				.WriteUInt32(sceneWord) // [55:59] -> obj+0x54
				.WriteArrayUInt32Length(sceneBlob) // [59:87]
				.WriteUInt32(0xFFFFFFFFu)
				.WriteUInt32(0xFFFFFFFFu)
				.WriteUInt32(fieldC) // per-MAP, 0 on 1010/2010/2040/5007 and 6 on 11040 (#451)
				.WriteUInt32(flags)
				.ToArray();
		}

		// RegenIIR (0x28D0AC99): an entity regen record - three resource/regen values. 21 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 v1][u32 v2][u32 v3]
		// RegenIIR_c::ReadSubClass (Ghidra FUN_0079dea0) reads exactly three big-endian u32 (via the ReadU32 helper
		// FUN_0048f900) into fields +0x2c/+0x30/+0x34 - the three consecutive resource pools (the health/stamina/mana
		// regen family); the single `flag` byte belongs to the base IIR and is 0 in every capture. Decoded by
		// tools/capture/decode_create_iir_records.py + tools/ghidra/DumpRegenReader.java and validated byte-exact
		// against 590 records across the login/gameplay/create captures (all type 0xC350, flag 0, three clean u32).
		public static byte[] BuildRegenIir(uint handle, uint v1, uint v2, uint v3, byte flag = 0, uint type = EntityClass.Character)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).WriteUInt32(v1).WriteUInt32(v2).WriteUInt32(v3).ToArray();
		}

		// SetMorphValueIIR (0xBADF5A4B): sets one morph/resource value on an entity. Variable length:
		//   [u32 type][u32 handle][u16 0][u8 blobLen][0x2A][name utf8][f32 weight BE][u8 p][u32 q]
		// `blobLen` = 1 + name.Length (it counts the 0x2A tag + the name). The editor streams the chosen
		// hair/texture resource refs ("hairmesh_207920", "texscript_183875") this way; `weight` is 1.0 in every
		// capture and the small p/q trailing pair carries morph flags/index. Raw-body float = big-endian.
		public static byte[] BuildSetMorphValueIir(uint handle, string name, float weight, byte p, uint q, uint type = EntityClass.Character)
		{
			var nameBytes = Encoding.Latin1.GetBytes(name); // wire strings are raw bytes - see FaolanStream.ReadString
			return new FaolanStream()
				.WriteUInt32(type)
				.WriteUInt32(handle)
				.WriteUInt16(0x0000)
				.WriteByte(1 + nameBytes.Length)
				.WriteByte(0x2A)
				.WriteArray(nameBytes)
				.WriteFloat(weight)
				.WriteByte(p)
				.WriteUInt32(q)
				.ToArray();
		}

		// SetNameIIR (0xADCE0CDA): the create-editor "name accepted" record echoing the chosen character name back so
		// the client advances to enter-world (dikketest idx 359, SendNicknameAccepted). RECORD:
		//   [u32 type][u32 handle][u32 0][u8 0x03][u8 0x01][u32 0][u16 len][UTF-8 name]
		public static byte[] BuildSetNameIir(uint handle, string name, uint type = EntityClass.Character)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt32(0u).WriteByte(0x03).WriteByte(0x01).WriteUInt32(0u).WriteStringByteLength(name).ToArray();
		}

		// StatIIR (0xF98E10B3): a single stat set on an entity. 17 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 statId][u32 value]
		// flag is 0 or 1 in every capture (set vs. continue/append - the editor streams runs of flag=1 for one stat
		// id with stepping values). type is CharacterWireType for the avatar's per-stat stream.
		public static byte[] BuildStatIir(uint type, uint handle, byte flag, uint statId, uint value)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).WriteUInt32(statId).WriteUInt32(value).ToArray();
		}

		// StatUpdateIIR (0x96B8DC59): a batch of stat (statId, value) pairs set on an entity:
		//   [u32 type][u32 handle][u32 0][u8 count][ count x ([u32 statId][u32 value]) ]
		// The editor/world-prep streams it to seed a character's stat block (max health/mana/... + a few flags).
		public static byte[] BuildStatUpdateIir(uint handle, (uint StatId, uint Value)[] stats, uint type = EntityClass.Character)
		{
			var s = new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt32(0u).WriteByte(stats.Length);
			foreach (var (statId, value) in stats)
				s.WriteUInt32(statId).WriteUInt32(value);
			return s.ToArray();
		}

		// In-WORLD SetNameIIR (0xADCE0CDA), grammar recovered byte-exact from n3InfoItemRemote::Construct (FUN_005b9d30)
		// + SetNameIIR_t::ReadSubClass (FUN_007ae1d0), #363 - the live-entity path, DISTINCT from BuildSetNameIir above
		// (the create-editor's reader). Construct reads the identity + an isToBePassed flag BEFORE the subclass:
		//   [u32 type][u32 handle][u8 isToBePassed=0]   <- Construct (isToBePassed must be 0 or 1)
		//   [u32 nameType=3][u8 0x01][u8 0x01]          <- ReadSubClass fields (this+0x48 u32, this+0x4c/0x4e bools)
		//   [u32 nameLen][ASCII name]                   <- nameLen read as a U32, HARD-CAPPED at 0x20 (must be 1..31),
		//                                                  else the client reads len 0, skips the name and rejects the
		//                                                  blob. Omitting the isToBePassed byte shifts everything by one
		//                                                  and corrupts nameLen (the "datasize left in stream" reject).
		// nameType (this+0x48) is applied by Activate->FUN_006ed100(nameType, this+0x4e): it MUST be 1..4 (0 hits the
		// switch default and the name is silently DROPPED), and 3 or 4 (with the last bool set) also REFRESH the live
		// nameplate (FUN_006dbc00/FUN_00447ba0) - the entity's OverheadText was built from the LDB hash at spawn and
		// only re-reads the custom name on that refresh. So 3/1/1 is what makes an already-spawned NPC show the name.
		public static byte[] BuildWorldSetNameIir(uint handle, string name, uint type = EntityClass.Character)
		{
			var bytes = Encoding.Latin1.GetBytes(name ?? "");
			if (bytes.Length > 31)
				bytes = bytes[..31];
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(0x00).WriteUInt32(3u).WriteByte(0x01).WriteByte(0x01).WriteArrayUInt32Length(bytes).ToArray();
		}
	}
}
