using Faolan.Core.Data;

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
		// The wire type tag every per-character editor record carries (the GameClient/character class id, 0xC350).
		public const uint CharacterWireType = 0x0000C350;

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
	}
}
