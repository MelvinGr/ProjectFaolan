using System;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

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

		// Reconstructs the SimpleChar record from its own Iir-carrier BODY (`body` = the bytes after the carrier's
		// [u32 len][u32 iirType] prefix: [u32 0x0000C350][u32 handle][u32 0] 2C ...), threading `playfieldInstance`.
		// Returns the input unchanged if it can't be framed/decoded (safety fallback - the verbatim bytes ship).
		//
		// ⚠️ It takes a BODY and returns a whole RECORD - `[u32 SimpleCharFullUpdateIIR][body]`, the shape the burst
		// sender frames directly (#542). The asymmetry is deliberate: the captured pedestal bodies predate the codec
		// and are stored without their type word, but a caller must not have to name the IIR to send one back.
		public static byte[]? Reconstruct(byte[]? body, uint playfieldInstance)
		{
			var record = body;
			if (record == null || record.Length < SpawnPacketBuilder.RotWOffset - 8 + 4)
				return record == null ? null : WithSubOpcode(record);

			// Frame as the [u32 len][u32 FA015807][record] template SpawnPacketBuilder consumes/produces.
			var template = new byte[8 + record.Length];
			template.WriteU32Be(0, (uint)(4 + record.Length));
			template.WriteU32Be(4, SimpleCharFullUpdateCodec.Opcode);
			record.CopyTo(template, 8);

			if (!SpawnBodyReader.TryReadCharacterMessage(record, out var read))
				return WithSubOpcode(record);

			var handle = record.ReadU32Be(4);
			var full = SpawnPacketBuilder.BuildSpawn(template, handle, PositionOf(template), RotationOf(template), read.Message);
			// The token patch works on the RECORD (past the carrier's u32 length), so slice it out, patch it and copy
			// it back. Best-effort: the captured token stays if the transform cannot be located.
			var patched = full[4..];
			if (SpawnTransformPatch.TryRewritePlayfield(patched, playfieldInstance))
				patched.CopyTo(full, 4);

			return full[4..];
		}

		// The LIVE avatar preview the editor scene puts on its pedestal: a captured SimpleChar record whose only
		// session-varying field is the HANDLE, so it is spliced rather than reconstructed (the pedestals go through
		// `Reconstruct`; this one carries the client instance, which no capture can supply). `tail` is everything
		// after the handle, verbatim.
		public static byte[] LivePreview(uint handle, byte[] tail)
		{
			using var s = new FaolanStream();
			return s.WriteUInt32(SimpleCharFullUpdateCodec.Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteArray(tail).ToArray();
		}

		// `[u32 SimpleCharFullUpdateIIR][body]` - the record a caller can send without naming the IIR itself.
		private static byte[] WithSubOpcode(byte[] body)
		{
			using var s = new FaolanStream();
			return s.WriteUInt32(SimpleCharFullUpdateCodec.Opcode).WriteArray(body).ToArray();
		}

		private static Vector3 PositionOf(byte[] template)
		{
			return new Vector3(template.ReadF32Le(SpawnPacketBuilder.PosXOffset),
				template.ReadF32Le(SpawnPacketBuilder.PosYOffset), template.ReadF32Le(SpawnPacketBuilder.PosZOffset));
		}

		private static Quaternion RotationOf(byte[] template)
		{
			return new Quaternion(template.ReadF32Le(SpawnPacketBuilder.RotXOffset),
				template.ReadF32Le(SpawnPacketBuilder.RotYOffset), template.ReadF32Le(SpawnPacketBuilder.RotZOffset),
				template.ReadF32Le(SpawnPacketBuilder.RotWOffset));
		}
	}
}
