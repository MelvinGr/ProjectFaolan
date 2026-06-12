using System;
using System.Buffers.Binary;

namespace Faolan.GameServer.Spawn
{
	// The NPC SimpleCharFullUpdate (FA015807) post-IIR scaffold - the stream that follows the embedded
	// ProtoCoreCharacterIIR. Unlike the player's post-IIR (PlayerSpawnScaffoldCodec, keyed on the 0x3F1
	// buff-table multiple), the NPC post-IIR is its own grammar (dissected 2026-06-12, workbench
	// Captures/dissect_npc_spawn.py over the Strie beach burst, 42 NPCs):
	//
	//   header:  [u32 0][u32 firstRecordLen = len(post) - 36][u32 recordCount]
	//            - word0 is 0 on ALL 42 (the player has 2 here).
	//            - word1 == len(post) - 36 on the single-record blocks (18/42); the larger blocks concatenate
	//              additional RUNTIME-STATE records (current movement / waypoint vec3s, active buff/timer pairs)
	//              past the first, so word1 covers only the first record group, not the whole stream.
	//            - word2 (recordCount) is 1 on 41/42.
	//   body:    per-record groups (movement anchors as big-endian vec3 float triples, buff/timer pairs), all
	//            terminated by the shared 0x3F1-marked empty pair lists.
	//
	// A FRESHLY-SPAWNED NPC (no accrued movement/buff state) has the MINIMAL form: a single constant 41-byte
	// block (23/42 NPCs carry exactly this, byte-identical - see MinimalPostIir). That is what a from-scratch
	// construction emits; the variable runtime-state forms are session state the server re-derives at the tick,
	// not template data, so they are NOT reproduced at spawn. NOTE: the wire-flip to emit this generated post-IIR
	// in place of the row's captured one is IN-CLIENT-GATED (a cross-session NPC body is proven rejected by the
	// client - see [[world-packets-construct-not-replay]]); this codec is the regeneration foundation, validated
	// byte-exact here, ready for that in-client step.
	public static class NpcSpawnScaffold
	{
		// word0 is constant 0 across the corpus (the player spawn has 2 in this slot).
		private const uint HeaderWord0 = 0;

		// The minimal post-IIR length, in bytes (the constant freshly-spawned form).
		public const int MinimalPostIirLength = 41;

		// firstRecordLen = len(post) - 36 (the header's self-length of the first record group).
		private const int RecordLenBias = 36;

		// The single record group in the minimal form (recordCount == 1).
		private const uint MinimalRecordCount = 1;

		// The minimal record body + the shared 0x3F1 terminators (everything after the 12-byte header). Constant
		// across all 23 NPCs that carry the minimal form (verified single distinct value, dissect_npc_spawn.py).
		private static readonly byte[] MinimalBody =
		{
			0x00, 0x00, 0x00, 0x03, 0xF1, // [u8 0][u32 0x3F1] - empty ID-list terminator
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, // empty pair list, 0x3F1 terminator
			0x00, 0x00, 0x03, 0xF1, // 0x3F1 marker
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1 // end pairs, final 0x3F1 marker
		};

		// Builds the minimal post-IIR scaffold for a freshly-spawned NPC: [u32 0][u32 len-36][u32 1] + the
		// constant terminator body. Byte-identical to the 23 minimal-form NPCs in the beach burst.
		public static byte[] BuildMinimalPostIir()
		{
			var post = new byte[MinimalPostIirLength];
			BinaryPrimitives.WriteUInt32BigEndian(post.AsSpan(0, 4), HeaderWord0);
			BinaryPrimitives.WriteUInt32BigEndian(post.AsSpan(4, 4), (uint)(MinimalPostIirLength - RecordLenBias));
			BinaryPrimitives.WriteUInt32BigEndian(post.AsSpan(8, 4), MinimalRecordCount);
			MinimalBody.CopyTo(post, 12);
			return post;
		}

		// Replaces an FA015807 body's post-IIR scaffold with the minimal session-independent form, adjusting the
		// inner body-length field (@0, which tracks total body size 1:1 - same convention as
		// SpawnPacketBuilder.SetCharacterMessage). A cross-session NpcSpawn row's captured post-IIR carries another
		// session's runtime state (movement waypoints / buff timers) that the client rejects; swapping in the
		// minimal form is the candidate fix for the cross-session render rejection (see
		// [[world-packets-construct-not-replay]]). No-op (returns the body unchanged) if no CharacterIIR is found.
		public static byte[] WithMinimalPostIir(byte[] body)
		{
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return body;

			var postStart = read.Offset + read.Length;
			var minimal = BuildMinimalPostIir();

			var result = new byte[postStart + minimal.Length];
			Array.Copy(body, 0, result, 0, postStart); // header + transform + pre-IIR + the IIR itself (unchanged)
			minimal.CopyTo(result, postStart);

			var delta = result.Length - body.Length;
			var bodyLength = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(0, 4));
			BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)(bodyLength + delta));
			return result;
		}
	}
}
