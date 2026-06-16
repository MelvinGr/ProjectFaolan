using Faolan.Core.Data;
using Faolan.Core.Network;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Constructive builders for the fixed-grammar IIR records the character-create EDITOR streams on its
	// GameAgent->GameInterface 0x2000 conversation (issue #189). Decoded by Captures/decode_create_editor.py and
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

		// StatIIR (0xF98E10B3): a single stat set on an entity. 17 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 statId][u32 value]
		// flag is 0 or 1 in every capture (set vs. continue/append - the editor streams runs of flag=1 for one stat
		// id with stepping values). type is CharacterWireType for the avatar's per-stat stream.
		public static byte[] BuildStatIir(uint type, uint handle, byte flag, uint statId, uint value)
		{
			return new FaolanStream()
				.WriteUInt32(type)
				.WriteUInt32(handle)
				.WriteByte(flag)
				.WriteUInt32(statId)
				.WriteUInt32(value)
				.ToArray();
		}

		// EnergyIIR (0x40749B8A): an entity energy/value record. 13 bytes:
		//   [u32 type][u32 handle][u8 flag][u32 value]
		public static byte[] BuildEnergyIir(uint type, uint handle, byte flag, uint value)
		{
			return new FaolanStream()
				.WriteUInt32(type)
				.WriteUInt32(handle)
				.WriteByte(flag)
				.WriteUInt32(value)
				.ToArray();
		}

		// PlayfieldFIIR (0x5D85BFC7, "MapChange"): announces the playfield the client is entering - its reference
		// position (a Vec3, raw-body big-endian floats), the map id, the entering character instance, and the live
		// per-session playfield token (carried TWICE). 103 bytes; framing decoded from the create captures
		// (docs/create-editor-bursts.md). `count` (3/6), `nearClip`=15.0 + `farClip` (~12-14), the trailing `flags`
		// and `field58` (a u32 at [58:62] - 0 in some captures, 0x01000000 in others) are not yet semantically
		// pinned, so they're passed through. Constructing this drives the map change from the Map row + the session
		// token instead of replaying the captured MapChange.
		public static byte[] BuildPlayfieldFiir(float posX, float posY, float posZ, uint mapId, uint charInstance,
			uint count, uint playfieldToken, float nearClip, float farClip, uint flags, uint field58 = 0u)
		{
			return new FaolanStream()
				.WriteUInt32(0u).WriteUInt32(0u).WriteByte((byte)0x00) // [0:9] prefix
				.WriteFloatBigEndian(posX).WriteFloatBigEndian(posY).WriteFloatBigEndian(posZ) // [9:21] reference position
				.WriteByte((byte)0x62)
				.WriteUInt32(0x0000C79Cu)
				.WriteUInt32(mapId)
				.WriteUInt32(0u)
				.WriteUInt32(charInstance)
				.WriteUInt32(0u)
				.WriteUInt32(count)
				.WriteUInt32(0x00009C50u) // 0x9C50 = 40016 (distinct from EntityClass.Character 0xC350; semantics TBD)
				.WriteUInt32(playfieldToken)
				.WriteUInt32(0u).WriteUInt32(field58) // [54:62]: a zero u32 then the un-pinned field58
				.WriteByte((byte)0x18)
				.WriteUInt32(0x00000001u)
				.WriteUInt32(playfieldToken) // token again
				.WriteUInt32(0x0000CB20u)
				.WriteFloatBigEndian(nearClip)
				.WriteFloatBigEndian(farClip)
				.WriteUInt32(0u)
				.WriteUInt32(0xFFFFFFFFu).WriteUInt32(0xFFFFFFFFu)
				.WriteUInt32(0x00000007u)
				.WriteUInt32(flags)
				.ToArray();
		}

		// GameTimeIIR (0x4F57DC08, "SetDayTime"): the in-game day-time + a wall-clock timestamp + the time rate.
		// 29 bytes: [u32 0][u32 0][u64 dayTime][u8 flag][u64 timestamp][f32 rate]. Decoded from the create captures
		// (docs/create-editor-bursts.md); dayTime's low 32 bits are the fractional half (0 / 0x80000000), flag is a
		// small phase byte (00/01/02), and the rate is the day-cycle speed (1.0 in every capture). Raw-body float =
		// big-endian. Constructing it lets the server drive the sky/lighting time instead of replaying the capture.
		public static byte[] BuildGameTimeIir(ulong dayTime, byte flag, ulong timestamp, float rate = 1.0f)
		{
			return new FaolanStream()
				.WriteUInt32(0u)
				.WriteUInt32(0u)
				.WriteUInt64(dayTime)
				.WriteByte(flag)
				.WriteUInt64(timestamp)
				.WriteFloatBigEndian(rate)
				.ToArray();
		}
	}
}
