using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The character-create EDITOR scene characters - the SimpleCharFullUpdateIIR (FA015807) spawns the initial burst
	// streams for the five archetype pedestals plus the live avatar preview (issue #189).
	//
	// They are ordinary entity spawns, the SAME grammar as the login/world-entry player spawn, so each one goes
	// through `SimpleCharFullUpdateCodec` - PARSED into the typed record and re-BUILT, with the session's values
	// (the playfield instance, the live handle) set on the model rather than patched into bytes. Nothing inside the
	// record is carried verbatim any more (#364): the pre-IIR scaffold is `SpawnPreIir`'s 53 NAMED stat words and
	// the post-IIR is `SpawnPostIir`'s typed stream, both decoded whole by #466/#475/#477. The parse+rebuild is
	// byte-exact on all six records (`CreateEditorSceneCharTests`), which is what says the model is complete.
	//
	// What this is NOT is DB-sourced. The typed values still come from each record's own bytes, so the editor scene
	// is a capture-shaped constant rather than content - the remaining half of #364, and it needs the scene modelled
	// as rows (five archetype characters + their poses), not more decode.
	public static class CreateEditorCharBuilder
	{
		// The create-editor session's playfield instance (the MapChange token), shared by every editor record.
		public const uint EditorPlayfieldInstance = 0x00502276;

		// Reconstructs the SimpleChar record from its own Iir-carrier BODY (`body` = the bytes after the carrier's
		// [u32 len][u32 iirType] prefix: [u32 0x0000C350][u32 handle][u32 0] 2C ...), threading `playfieldInstance`.
		// Returns the bytes unchanged if they cannot be decoded (safety fallback - the record still ships).
		//
		// ⚠️ It takes a BODY and returns a whole RECORD - `[u32 SimpleCharFullUpdateIIR][body]`, the shape the burst
		// sender frames directly (#542). The asymmetry is deliberate: the captured pedestal bodies predate the codec
		// and are stored without their type word, but a caller must not have to name the IIR to send one back.
		public static byte[]? Reconstruct(byte[]? body, uint playfieldInstance)
		{
			if (body == null)
				return null;

			var record = WithSubOpcode(body);
			SimpleCharFullUpdateIIR value;
			try
			{
				value = SimpleCharFullUpdateCodec.Parse(record);
			}
			catch (CodecParseException)
			{
				return record;
			}

			value.Playfield = playfieldInstance;
			return SimpleCharFullUpdateCodec.Build(value);
		}

		// The LIVE avatar preview on its pedestal: the same parse-and-rebuild, with the session's client instance as
		// the entity HANDLE - which is the one thing no capture can supply. `tail` is the captured record from
		// `FieldA` on (i.e. past the subop / class / handle words).
		public static byte[] LivePreview(uint handle, byte[] tail)
		{
			using var head = new FaolanStream();
			var record = head.WriteUInt32(SimpleCharFullUpdateCodec.Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteArray(tail).ToArray();

			SimpleCharFullUpdateIIR value;
			try
			{
				value = SimpleCharFullUpdateCodec.Parse(record);
			}
			catch (CodecParseException)
			{
				return record;
			}

			value.Handle = handle;
			return SimpleCharFullUpdateCodec.Build(value);
		}

		// `[u32 SimpleCharFullUpdateIIR][body]` - the record a caller can send without naming the IIR itself.
		private static byte[] WithSubOpcode(byte[] body)
		{
			using var s = new FaolanStream();
			return s.WriteUInt32(SimpleCharFullUpdateCodec.Opcode).WriteArray(body).ToArray();
		}
	}
}
