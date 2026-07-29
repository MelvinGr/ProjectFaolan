using System.Text;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.World
{
	// FanaticCommandIIR_t (0xEE4AA85C) - what the client sends when a GM runs one of its ~133 GM commands. "Fanatic" is
	// the client's own name for its GM/admin subsystem (it also names the `Fanatic` debug channel and the tweak table's
	// TWK_karma_Fanatic* entries), not anything to do with the player-facing game.
	//
	// `/gm` specifically is FanaticGMLevelCommand_c - the command that reads and sets the privilege MASK the whole
	// surface is gated on, and the one command registered without a permission record so anyone can reach it. The mask,
	// the 133-command catalog and each command's required bit are in docs/subsystems/gm-commands.md.
	//
	// GRAMMAR - measured against real bytes from a live GM session (2026-07-29), not inferred:
	//
	//   [u32 reclen][u32 subop][u32 class][u32 handle]   the 0x2000 IIR carrier
	//   [u8 flag]                                        the carrier's trailing flag byte, always 0 so far
	//   [u32 word0][u32 targetA][u32 targetB]            ReadSubClass's obj+0x2C / +0x30 / +0x34
	//   [u32 textLength][textLength bytes]               the command string, LATIN-1
	//
	// `/cloak`               -> flag 0, word0 2, targets 0/0, len 6,  "/cloak"
	// `/teleport gm island`  -> flag 0, word0 2, targets 0/0, len 19, "/teleport gm island"
	//
	// ⚠️ The FLAG BYTE is what made every command parse as empty, and it is easy to lose: it belongs to the carrier
	// (GameActionListCodec writes the same `[u8 0]` after its handle), not to this record, so a reader that starts the
	// record at the handle is off by ONE and every field after it slides. The symptom was not a parse failure but
	// plausible garbage - `word0 0, targets 33554432/0, text ''`, where 33554432 is 0x02000000 = word0's 2 read one
	// byte early. ⚠️ It is also NOT the case that the client's reader and writer disagree - a previous fix "corrected"
	// this by dropping the target pair, on a misreading of the write path. ReadSubClass (slot 8, FUN_00663860) is
	// right: three u32s then `BinaryStream::ReadString` (FUN_00663c20).
	//
	// - `word0` is **2** for every command observed - it is not a command id, and the command itself is entirely in the
	//   TEXT. So dispatch is a string parse, exactly as the client's own log ("Received command string : <text>") says.
	// - If BOTH targetA and targetB are 0 the client substitutes the pair at obj+8, so 0/0 means "no explicit target",
	//   which is what a self-targeted command sends.
	//
	// ⚠️ The text's length prefix is a **u32**, not the u16 our wire strings normally use - `BinaryStream::ReadString`
	//   reads four bytes and rejects anything over 0xFFFFFF with "string size is too long". Reading it as a u16 takes
	//   the HIGH half of the length, which is 0 for every real command, so the argument silently parsed as empty.
	//
	// - If BOTH targetA and targetB read as 0 the client substitutes the pair at obj+8 - an unspecified target defaults
	//   to something the object already carries (self, most likely).
	// - The trailing text is what the client's own log calls the COMMAND STRING: ReadSubClass logs
	//   `FanaticCommandIIR_t::ReadSubClass(): Received command string : <text>` on the `Fanatic` channel at level 2
	//   (enable it with `--debug-channels Fanatic`), which is the cheapest way to see its view of what it sent.
	//
	// ⚠️ `word0` (obj+0x2C) is NOT identified. It is written and read first, and the client's own naming attributes the
	//   command itself to the trailing STRING, so calling this field "the command" would be a guess.
	//
	// ⚠️ PARSE ONLY, deliberately. What the server is supposed to send BACK is not decoded: no capture in the corpus
	// contains a FanaticCommandIIR (the retail traces are ordinary play, not GM sessions), so any reply would be
	// invented. Parsing it at least turns "Unknown 0x2000 Opcode: 0xEE4AA85C" into the command the client asked for,
	// which is what a reply has to be built from. Open work: Gitea #448/#449.
	public static class FanaticCommandCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FanaticCommandIir;

		/// <summary>
		///     The client's own ceiling on the text length (`BinaryStream::ReadString` rejects anything larger), used
		///     here as the same sanity bound rather than trusting a length off the wire.
		/// </summary>
		private const uint MaxTextLength = 0xFFFFFF;

		public static Parsed? TryParse(byte[] body)
		{
			// [u32 len][u32 subop][u32 class][u32 handle] then the record; be defensive - this is an undecoded path and
			// a short/odd body must return null rather than throw into the inbound loop.
			if (body.Length < 16)
				return null;

			var s = new FaolanStream(body);
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // entity class
			var handle = s.ReadUInt32();

			// The carrier's flag byte, then the record. Skipping it is what slid every field by one and turned the
			// command text into nothing (see the grammar note above).
			if (s.Position + 13 > body.Length)
				return null;

			var flag = s.ReadByte();
			var word0 = s.ReadUInt32();
			var targetA = s.ReadUInt32();
			var targetB = s.ReadUInt32();

			// The command text: [u32 length][bytes]. Bounds-check against the BODY rather than trusting the declared
			// length - this is a path we cannot round-trip against a capture, so a malformed body must degrade to
			// "no text" rather than throw into the inbound loop.
			var text = "";
			if (s.Position + 4 <= body.Length)
			{
				var length = s.ReadUInt32();
				if (length <= MaxTextLength && s.Position + length <= body.Length)
					text = Encoding.Latin1.GetString(body, (int)s.Position, (int)length);
			}

			return new Parsed(handle, flag, word0, targetA, targetB, text);
		}

		public readonly record struct Parsed(uint Handle, byte Flag, uint Word0, uint TargetA, uint TargetB, string Text);
	}
}
