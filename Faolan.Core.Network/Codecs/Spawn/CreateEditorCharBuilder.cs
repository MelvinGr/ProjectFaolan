using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Reconstructs the character-create EDITOR scene-character records - the SimpleCharFullUpdateIIR (FA015807)
	// spawns the initial burst streams for the editor pedestals (issue #189). They are ordinary entity spawns:
	// the SAME grammar as the login/world-entry player spawn, so each is rebuilt through SpawnPacketBuilder -
	// the TRANSFORM from the typed ProtoVec3/ProtoQuat pose and the IDENTITY by decoding + re-encoding the
	// ProtoCoreCharacterIIR (both byte-identical round-trips). The session HANDLE is preserved and the per-session
	// PLAYFIELD INSTANCE (the transform's `18 <varint>`) is threaded in. The still-un-lifted pre-IIR scaffold
	// block + post-IIR scaffold are carried verbatim from the record itself (#364): the pedestals' pre-IIR holds
	// irreducible per-session heap pointers/handles (no DB source), and 5 of 6 post-IIR tables are the larger
	// runtime-state form NpcSpawnAppearanceCodec refuses - so byte-exact-from-DB is impossible; retiring the
	// self-reconstruction needs a #210-style construct-minimal + in-client pass, not the #185 lift (which only
	// covers the clean-appearance shape). Reconstructing a record from its own bytes reproduces it byte-for-byte
	// (CreateEditorSceneCharTests), so this is a construction oracle, not a replay: the transform + identity are
	// genuinely rebuilt, and the playfield is parameterized for the live session.
	public static class CreateEditorCharBuilder
	{
		// The create-editor session's playfield instance (the MapChange token), shared by every editor record.
		public const uint EditorPlayfieldInstance = 0x00502276;

		// Reconstructs the SimpleChar record from its own Iir-carrier body (`record` = the bytes after the carrier's
		// [u32 len][u32 iirType] prefix: [u32 0x0000C350][u32 handle][u32 0] 2C ...), threading `playfieldInstance`.
		// Returns the input unchanged if it can't be framed/decoded (safety fallback - the verbatim bytes ship).
		public static byte[]? Reconstruct(byte[]? record, uint playfieldInstance)
		{
			if (record == null || record.Length < SpawnPacketBuilder.RotWOffset - 8 + 4)
				return record;

			// Frame as the [u32 len][u32 FA015807][record] template SpawnPacketBuilder consumes/produces.
			var template = new byte[8 + record.Length];
			BinaryPrimitives.WriteUInt32BigEndian(template.AsSpan(0, 4), (uint)(4 + record.Length));
			BinaryPrimitives.WriteUInt32BigEndian(template.AsSpan(4, 4), 0xFA015807u);
			record.CopyTo(template, 8);

			if (!PlayerSpawnReader.TryReadCharacterMessage(record, out var read))
				return record;

			var handle = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4, 4));
			var full = SpawnPacketBuilder.BuildSpawn(template, handle, PositionOf(template), RotationOf(template), read.Message);
			SpawnPlayfieldRewriter.TryRewrite(full, playfieldInstance); // best-effort: keeps the captured token if it can't
			return full[8..];
		}

		private static Vector3 PositionOf(byte[] template)
		{
			return new Vector3(BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(SpawnPacketBuilder.PosXOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(SpawnPacketBuilder.PosYOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(SpawnPacketBuilder.PosZOffset, 4)));
		}

		private static Quaternion RotationOf(byte[] template)
		{
			return new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(SpawnPacketBuilder.RotXOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(SpawnPacketBuilder.RotYOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(SpawnPacketBuilder.RotZOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(SpawnPacketBuilder.RotWOffset, 4)));
		}
	}
}
