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
	// GRAMMAR. ⚠️ The client's reader and writer do NOT agree, and this used to claim they did - which is why every
	// inbound command parsed as empty. They are the two DIRECTIONS, and only the writer describes what we receive:
	//
	//   c2s (what we parse)  [u32 word0][u32 textLength][textLength bytes]
	//   s2c (what it expects) [u32 word0][u32 targetA][u32 targetB][string]
	//
	// - WriteSubClass (slot 9, FUN_00663ae0) - the SEND path, so this is the inbound layout - emits obj+0x2C through
	//   the FanaticIIR_t base write (FUN_00663450, which writes that ONE u32 and nothing else), then the command text
	//   as a u32 length followed by the bytes. There is no target pair on the wire in this direction.
	// - ReadSubClass (slot 8, FUN_00663860) is the RECEIVE path: three u32s into obj+0x2C/0x30/0x34 - and if targetA
	//   and targetB both read 0 it substitutes the pair at obj+8 - then the text via `BinaryStream::ReadString`
	//   (FUN_00663c20). That shape is what a server REPLY has to be built in, not what a command arrives in.
	//
	// Parsing inbound with the s2c layout consumed two extra u32s before the length, so the length was read out of the
	// middle of the command string and the text always came out empty: a `/cloak` and a `/teleport` both reported
	// `word0 0, targets 33554432/0, text ''` - 33554432 is 0x02000000, i.e. the misread length word.
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

			if (s.Position + 4 > body.Length)
				return null;

			var word0 = s.ReadUInt32();

			// The command text: [u32 length][bytes], immediately after word0 - the base write emits no target pair in
			// this direction. Bounds-check against the BODY rather than trusting the declared length: this is a path we
			// cannot round-trip against a capture, so a malformed body must degrade to "no text" rather than throw into
			// the inbound loop.
			var text = "";
			if (s.Position + 4 <= body.Length)
			{
				var length = s.ReadUInt32();
				if (length <= MaxTextLength && s.Position + length <= body.Length)
					text = Encoding.Latin1.GetString(body, (int)s.Position, (int)length);
			}

			return new Parsed(handle, word0, text);
		}

		public readonly record struct Parsed(uint Handle, uint Word0, string Text);
	}
}
