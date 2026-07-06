using System;
using System.Text;
using Faolan.Core;
using Faolan.Core.Network;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer.CreateFlow
{
	// Constructive builders for the fixed-grammar IIR records the character-create EDITOR streams on its
	// GameAgent->GameInterface 0x2000 conversation (issue #189). Decoded by Captures/scripts/decode_create_editor.py and
	// validated byte-exact against every create capture (docs/create-editor-bursts.md). These are the small,
	// fully-decoded records; the big entity full-updates (SimpleChar/SimpleDynel) reuse the existing spawn
	// builders (ProtoVec3/ProtoQuat transform + the recovered embedded protobufs) rather than a bespoke decoder.
	//
	// Each returns just the IIR RECORD (the bytes after the 0x2000 carrier's [u32 len][u32 iirType] prefix); the
	// caller frames it with WriteArrayUInt32Length(s => s.WriteUInt32(iirType).WriteArray(record)) as the existing
	// RetailCreateEditor*Burst Iir() helper already does.
	public static class CreateEditorRecordBuilder
	{
		// The entity-class tag every per-character editor record carries (EntityClass.Character = 0xC350).
		public const uint CharacterWireType = EntityClass.Character;

		// BehaviorControlCenterIIR (0x642CD3D6), LONG form (116 bytes): the same 28-byte head as the short form
		// above, plus an 88-byte behavior-state tail that binds the behavior to an EQUIPPED ITEM (the three records
		// in WorldPrep each pair with one of the burst's DispatchAddItem equipment pieces - the item's handle, its
		// 4-char entity tag and its transform all match):
		//   <head: short-form bytes, slot 9, msgLen 10, protobuf 08 <behaviorId> 18 <phase> 28 <field5> 30 <field6>>
		//   [u32 index][u32 someId][u32 itemHandle][u32 itemHandle][u32 0][u32 1][char[4] tag]
		//   [byte[16] transform][byte[16] transform][8-byte ownerStamp][8-byte ownerStamp][12 zero bytes]
		// The item handle + 4-char tag are repeated; the 16-byte transform is the item's pose packed as four
		// big-endian u32 (the same x/y/z + rotation values the paired DispatchAddItem carries as fixed32 protobuf
		// fields), also repeated; `ownerStamp` (0xB290805C 0x29E627CA) is the fixed creator/timestamp pair that
		// recurs across the whole WorldPrep burst (semantics unpinned). Decoded from WorldPrepBurst.bin by
		// Captures/scripts/decode_create_iir_records.py and locked byte-exact by CreateEditorRecordTests.
		private static readonly byte[] BehaviorOwnerStamp = [0xB2, 0x90, 0x80, 0x5C, 0x29, 0xE6, 0x27, 0xCA];

		// BehaviorControlCenterIIR (0x642CD3D6), registration form: registers one behavior on an entity.
		//   [u32 type][u32 handle][u16 0x0004][u32 slot][u32 msgLen][protobuf][optional 20-byte bound tail]
		// protobuf = base varint fields `08 <behaviorId> [10 <field2>] 18 <phase> 28 <field5> 30 <field6>
		// [58 <field11>] [60 <field12>]`, msgLen = its length. The optional fields (2/11/12) and the 20-byte
		// "bound" tail (`[u32 boundA][u32 boundB][12x 00]`) were decoded from the create-world cutscene burst
		// (Captures/scripts/decode_behaviorcc.py, #234): of its 90 records the field set is {1,3,5,6} with optional
		// {2,11,12} and a 0- or 20-byte tail. (A separate 88-byte item-bound long form has its own builder,
		// BuildBehaviorControlCenterIirLong below.) Decoded by Captures/scripts/decode_create_iir_records.py + decode_behaviorcc.py.
		// `form` is 0x0004 for the common registration; the rarer 0x0007 subtype adds protobuf field 4 (a varint,
		// between phase and field5). Protobuf wire order: f1=behaviorId, [f2], f3=phase, [f4], f5, f6, [f11], [f12].
		public static byte[] BuildBehaviorControlCenterIir(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6, uint type = CharacterWireType, uint? field2 = null, uint? field4 = null,
			uint? field11 = null, uint? field12 = null, uint? boundA = null, uint? boundB = null, ushort form = 0x0004)
		{
			var pb = new FaolanStream().WriteByte(0x08).WriteVarint(behaviorId);
			if (field2 is uint f2)
				pb.WriteByte(0x10).WriteVarint(f2);
			pb.WriteByte(0x18).WriteVarint(phase);
			if (field4 is uint f4)
				pb.WriteByte(0x20).WriteVarint(f4);
			pb.WriteByte(0x28).WriteVarint(field5).WriteByte(0x30).WriteVarint(field6);
			if (field11 is uint f11)
				pb.WriteByte(0x58).WriteVarint(f11);
			if (field12 is uint f12)
				pb.WriteByte(0x60).WriteVarint(f12);

			var s = new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt16(form).WriteUInt32(slot).WriteArrayUInt32Length(pb.ToArray());
			if (boundA is uint a)
				s.WriteUInt32(a).WriteUInt32(boundB ?? 0u).WriteArray(new byte[12]);
			return s.ToArray();
		}

		// The trailing 3 u32 (`trailingA/B/C`) are 0 in the WorldPrep equipment records but carry a small state pair
		// (e.g. 0,1,1) in the create-world cutscene's mount/companion long form - so they're parameterized (default 0).
		public static byte[] BuildBehaviorControlCenterIirLong(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6, uint index, uint someId, uint itemHandle, string tag, byte[] transform,
			uint type = CharacterWireType, uint trailingA = 0, uint trailingB = 0, uint trailingC = 0)
		{
			var tagBytes = Encoding.ASCII.GetBytes(tag);
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
				.WriteArray(BehaviorOwnerStamp)
				.WriteArray(BehaviorOwnerStamp)
				.WriteUInt32(trailingA)
				.WriteUInt32(trailingB)
				.WriteUInt32(trailingC)
				.ToArray();
		}

		// CharacterActionIIR (0x6B6FD368): grants one action/ability id to an entity. 41 bytes:
		//   [u32 type][u32 handle][u32 0][u8 0x1D][16x 0x00][u32 actionId][8x 0x00]
		// The create-editor WorldPrep burst streams one per granted id (the same ability/feat id space as the
		// player-spawn scaffold's id list - e.g. 0x050E24). Decoded by Captures/scripts/decode_character_action.py and
		// validated byte-exact against every record in the WorldPrep capture (CreateEditorRecordTests).
		public static byte[] BuildCharacterActionIir(uint handle, uint actionId, uint type = CharacterWireType)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt32(0u).WriteByte(0x1D).WriteArray(new byte[16]).WriteUInt32(actionId).WriteArray(new byte[8]).ToArray();
		}

		// ComboBookIIR (0xA8BBEB7F): grants one combo/ability to the entity's combo book. 14 bytes:
		//   [u32 type][u32 handle][u16 0x0001][u32 comboId]
		// The WorldPrep burst streams one per combo id. Decoded by Captures/scripts/decode_create_iir_records.py.
		public static byte[] BuildComboBookIir(uint handle, uint comboId, uint type = CharacterWireType)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt16(0x0001).WriteUInt32(comboId).ToArray();
		}

		// CharInPlayIIR (0x864CFEF8): the "character is now in play" signal. 9 bytes:
		//   [u32 type][u32 handle][u8 flag]
		// An EMPTY record - the client reader (FUN_0075a920) only resolves the SimpleChar for `handle` and logs; the
		// carrier handle is the whole message (docs/iir-readers-combat-world.md). flag is the base IIR's, 0 in every capture.
		public static byte[] BuildCharInPlayIir(uint handle, byte flag = 0, uint type = CharacterWireType)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).ToArray();
		}

		// EnergyIIR (0x40749B8A): an entity energy/value record. 13 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 value]
		public static byte[] BuildEnergyIir(uint type, uint handle, byte flag, uint value)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).WriteUInt32(value).ToArray();
		}

		// FeedbackIIR (0xBA1FD222): a UI feedback signal. 22 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 a][u32 b][u32 c][u8 d]
		// The client reader (FUN_0079f8e0) reads [u32x3][u8] after the base IIR flag (docs/iir-readers-conversation-misc.md);
		// the three words + trailing byte are an opaque numeric feedback payload, carried by value.
		public static byte[] BuildFeedbackIir(uint handle, byte flag, uint a, uint b, uint c, byte d, uint type = CharacterWireType)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).WriteUInt32(a).WriteUInt32(b).WriteUInt32(c).WriteByte(d).ToArray();
		}

		// GameTimeIIR (0x4F57DC08, "SetDayTime"): the in-game day-time + a wall-clock timestamp + the time rate.
		// 29 bytes: [u32 0][u32 0][u64 dayTime][u8 flag][u64 timestamp][f32 rate]. Decoded from the create captures
		// (docs/create-editor-bursts.md); dayTime's low 32 bits are the fractional half (0 / 0x80000000), flag is a
		// small phase byte (00/01/02), and the rate is the day-cycle speed (1.0 in every capture). Raw-body float =
		// big-endian. Constructing it lets the server drive the sky/lighting time instead of replaying the capture.
		public static byte[] BuildGameTimeIir(ulong dayTime, byte flag, ulong timestamp, float rate = 1.0f)
		{
			return new FaolanStream().WriteUInt32(0u).WriteUInt32(0u).WriteUInt64(dayTime).WriteByte(flag).WriteUInt64(timestamp).WriteFloat(rate).ToArray();
		}

		// PlayfieldFIIR (0x5D85BFC7, "MapChange"): announces the playfield the client is entering - its reference
		// position (a Vec3, raw-body big-endian floats), the map id, the entering character instance, and the live
		// per-session playfield token (carried TWICE). 103 bytes; framing decoded from the create captures
		// (docs/create-editor-bursts.md). `count` (3/6), `nearClip`=15.0 + `farClip` (~12-14), the trailing `flags`
		// and `field58` (a u32 at [58:62] - 0 in some captures, 0x01000000 in others) are not yet semantically
		// pinned, so they're passed through. Constructing this drives the map change from the Map row + the session
		// token instead of replaying the captured MapChange.
		public static byte[] BuildPlayfieldFiir(float posX, float posY, float posZ, uint mapId, uint charInstance, uint count, uint playfieldToken, float nearClip, float farClip, uint flags, uint field58 = 0u, uint fieldB = 0u)
		{
			return new FaolanStream()
				.WriteUInt32(0u)
				.WriteUInt32(0u)
				.WriteByte(0x00) // [0:9] prefix
				.WriteFloat(posX)
				.WriteFloat(posY)
				.WriteFloat(posZ) // [9:21] reference position
				.WriteByte(0x62)
				.WriteUInt32(0x0000C79Cu)
				.WriteUInt32(mapId)
				.WriteUInt32(0u)
				.WriteUInt32(charInstance)
				.WriteUInt32(0u)
				.WriteUInt32(count)
				.WriteUInt32(0x00009C50u) // 0x9C50 = 40016 (distinct from EntityClass.Character 0xC350; semantics TBD)
				.WriteUInt32(playfieldToken)
				.WriteUInt32(0u)
				.WriteUInt32(field58) // [54:62]: a zero u32 then the un-pinned field58
				.WriteByte(0x18)
				.WriteUInt32(0x00000001u)
				.WriteUInt32(playfieldToken) // token again
				.WriteUInt32(0x0000CB20u)
				.WriteFloat(nearClip)
				.WriteFloat(farClip)
				.WriteUInt32(fieldB) // [previously hardcoded 0]: beach capture carries 0x3, retail Old Tarantia 0x0
				.WriteUInt32(0xFFFFFFFFu)
				.WriteUInt32(0xFFFFFFFFu)
				.WriteUInt32(0x00000007u)
				.WriteUInt32(flags)
				.ToArray();
		}

		// RegenIIR (0x28D0AC99): an entity regen record - three resource/regen values. 21 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 v1][u32 v2][u32 v3]
		// RegenIIR_c::ReadSubClass (Ghidra FUN_0079dea0) reads exactly three big-endian u32 (via the ReadU32 helper
		// FUN_0048f900) into fields +0x2c/+0x30/+0x34 - the three consecutive resource pools (the health/stamina/mana
		// regen family); the single `flag` byte belongs to the base IIR and is 0 in every capture. Decoded by
		// Captures/scripts/decode_create_iir_records.py + Captures/ghidra/DumpRegenReader.java and validated byte-exact
		// against 590 records across the login/gameplay/create captures (all type 0xC350, flag 0, three clean u32).
		public static byte[] BuildRegenIir(uint handle, uint v1, uint v2, uint v3, byte flag = 0, uint type = CharacterWireType)
		{
			return new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteByte(flag).WriteUInt32(v1).WriteUInt32(v2).WriteUInt32(v3).ToArray();
		}

		// SetMorphValueIIR (0xBADF5A4B): sets one morph/resource value on an entity. Variable length:
		//   [u32 type][u32 handle][u16 0][u8 blobLen][0x2A][name utf8][f32 weight BE][u8 p][u32 q]
		// `blobLen` = 1 + name.Length (it counts the 0x2A tag + the name). The editor streams the chosen
		// hair/texture resource refs ("hairmesh_207920", "texscript_183875") this way; `weight` is 1.0 in every
		// capture and the small p/q trailing pair carries morph flags/index. Raw-body float = big-endian.
		public static byte[] BuildSetMorphValueIir(uint handle, string name, float weight, byte p, uint q, uint type = CharacterWireType)
		{
			var nameBytes = Encoding.UTF8.GetBytes(name);
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
		public static byte[] BuildStatUpdateIir(uint handle, (uint StatId, uint Value)[] stats, uint type = CharacterWireType)
		{
			var s = new FaolanStream().WriteUInt32(type).WriteUInt32(handle).WriteUInt32(0u).WriteByte(stats.Length);
			foreach (var (statId, value) in stats)
				s.WriteUInt32(statId).WriteUInt32(value);
			return s.ToArray();
		}
	}
}
