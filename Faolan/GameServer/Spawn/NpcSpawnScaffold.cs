using System;
using System.Buffers.Binary;
using Faolan.Core.Data;

namespace Faolan.GameServer.Spawn
{
	// The NPC SimpleCharFullUpdate (FA015807) post-IIR scaffold - the stream that follows the embedded
	// ProtoCoreCharacterIIR. Unlike the player's post-IIR (PlayerSpawnScaffoldCodec, keyed on the 0x3F1
	// buff-table multiple), the NPC post-IIR is its own grammar (dissected 2026-06-12, workbench
	// Captures/scripts/dissect_npc_spawn.py over the Strie beach burst, 42 NPCs):
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
	// A FRESHLY-SPAWNED ANIMAL (no accrued movement/buff state) has the MINIMAL form: a single constant 41-byte
	// block (23/42 NPCs carry exactly this, byte-identical - see MinimalPostIir). That is what a from-scratch
	// construction emits for animals. HUMANOIDS, however, carry a LARGER post-IIR holding their APPEARANCE / morph
	// table - the "*key=val" texscript string (e.g. "*ii=-1;*bl=-1") followed by [u32 morphId][float32] pairs,
	// decoded on Casilda (Gitea #243). Emitting the minimal form for a humanoid DROPS that table and the client
	// renders the NPC BALD - the "missing hair" regression ([[npc_spawn_minimal_postiir_breaks_humanoids]]). So the
	// humanoid post-IIR is NOT purely droppable runtime state, and the live path keeps it verbatim for humanoids
	// (WithFreshScaffolds, gated on worn equipment); only animals get the minimal post-IIR reset. (This corrects the
	// earlier conclusion in docs/fa015807-spawn-reader.md lines 115-130, which mis-classified the morph table as
	// runtime state from the population sweep.) The minimal codecs below remain the validated animal/from-scratch
	// foundation, byte-exact here.
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
		[
			0x00, 0x00, 0x00, 0x03, 0xF1, // [u8 0][u32 0x3F1] - empty ID-list terminator
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, // empty pair list, 0x3F1 terminator
			0x00, 0x00, 0x03, 0xF1, // 0x3F1 marker
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1 // end pairs, final 0x3F1 marker
		];

		// Builds the minimal post-IIR scaffold for a freshly-spawned NPC: [u32 0][u32 len-36][u32 1] + the
		// constant terminator body. Byte-identical to the 23 minimal-form NPCs in the beach burst.
		public static byte[] BuildMinimalPostIir()
		{
			return new FaolanStream()
				.WriteUInt32(HeaderWord0)
				.WriteUInt32((uint)(MinimalPostIirLength - RecordLenBias))
				.WriteUInt32(MinimalRecordCount)
				.WriteArray(MinimalBody)
				.ToArray();
		}

		// Replaces an FA015807 body's PRE-IIR scaffold (the 223-byte block between the transform and the embedded
		// CoreCharacterIIR) with the from-scratch minimal form (NpcSpawnPreIir.BuildMinimal: all runtime words 0
		// except the category word). The cross-capture sweep (Captures/scripts/classify_fa015807_fields.py) proved the
		// captured scaffold carries only per-instance runtime state (handles/timers), 0 on a fresh spawn - so a
		// cross-session row's captured scaffold holds another session's runtime state, and the minimal form is the
		// correct fresh-spawn value (Gitea #185). Length-preserving (FixedLength == FixedLength), so the body-length
		// header is unchanged. No-op (returns the body unchanged) for a count>0 captured scaffold (unobserved) or
		// when no CharacterIIR is found.
		public static byte[] WithMinimalPreIir(byte[] body)
		{
			if (!NpcSpawnPreIirCodec.TryDecode(body, out _, out var offset))
				return body;

			var result = (byte[])body.Clone();
			NpcSpawnPreIirCodec.Encode(NpcSpawnPreIir.BuildMinimal()).CopyTo(result, offset);
			return result;
		}

		// Applies both minimal scaffolds (pre-IIR then post-IIR), turning a captured (or cross-session) NPC body
		// into a from-scratch fresh-spawn body: the transform + CharacterIIR (identity/appearance) are kept, the
		// surrounding runtime-state scaffolds are reset to their idle-fresh form. This is the wire side of the
		// #185 "lift the scaffolds" work; the live flip is gated until verified in-client (NpcSpawnBuilder).
		public static byte[] WithMinimalScaffolds(byte[] body)
		{
			return WithMinimalPostIir(WithMinimalPreIir(body));
		}

		// The fresh-spawn scaffold pass used by the live spawn path. Like WithMinimalScaffolds, but a HUMANOID
		// keeps its captured post-IIR instead of collapsing to the 41-byte minimal form: a humanoid's post-IIR
		// carries that NPC's APPEARANCE / morph table (the "*key=val" texscript string + [morphId][float] pairs -
		// decoded on Casilda, Gitea #243), and the minimal form drops it, so the client renders the NPC bald. An
		// animal carries no such table (it ships exactly the minimal form), so it still gets the validated #185
		// minimal post-IIR reset. The pre-IIR (transform/bounds runtime block) is always reset to minimal for both.
		public static byte[] WithFreshScaffolds(byte[] body, bool humanoid)
		{
			body = WithMinimalPreIir(body);
			return humanoid ? body : WithMinimalPostIir(body);
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

	// Typed, mutable view of the NPC post-IIR scaffold header (the stream after the embedded CoreCharacterIIR):
	// [u32 word0][u32 firstRecordLen][u32 recordCount] followed by the per-record body. The header is decoded
	// into typed fields; the record body is kept RAW because it is session runtime state (movement anchors /
	// buff-timer pairs) the simulation re-derives at the tick, not template data (NpcSpawnScaffold above). The
	// minimal freshly-spawned form (word0=0, recordCount=1, the constant 0x3F1-terminated body) is what a
	// from-scratch construction emits; this typed view round-trips both that and the larger runtime-state forms
	// byte-exact, so the from-DB path owns the header grammar in code (Gitea #185).
	public sealed class NpcSpawnPostIir
	{
		// The 12-byte header ([u32 word0][u32 firstRecordLen][u32 recordCount]).
		private const int HeaderLength = 12;

		public uint Word0 { get; set; }
		public uint FirstRecordLen { get; set; }
		public uint RecordCount { get; set; } = 1;
		public byte[] Records { get; set; } = [];

		// Decodes the post-IIR scaffold from an FA015807 body (the bytes after the embedded CoreCharacterIIR,
		// located via PlayerSpawnReader). Returns false if no CharacterIIR is found or the post-IIR is too short
		// to hold the header.
		public static bool TryDecode(byte[] body, out NpcSpawnPostIir scaffold)
		{
			scaffold = null;
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var start = read.Offset + read.Length;
			if (start + HeaderLength > body.Length)
				return false;

			scaffold = new NpcSpawnPostIir
			{
				Word0 = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(start, 4)),
				FirstRecordLen = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(start + 4, 4)),
				RecordCount = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(start + 8, 4)),
				Records = body[(start + HeaderLength)..],
			};
			return true;
		}

		// Serializes the scaffold to its wire bytes (the 12-byte header + the raw record body).
		public byte[] Encode()
		{
			return new FaolanStream()
				.WriteUInt32(Word0)
				.WriteUInt32(FirstRecordLen)
				.WriteUInt32(RecordCount)
				.WriteArray(Records)
				.ToArray();
		}
	}
}
