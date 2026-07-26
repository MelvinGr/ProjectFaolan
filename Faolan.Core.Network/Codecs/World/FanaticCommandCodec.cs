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
	// GRAMMAR from the client's ReadSubClass (Ghidra FUN_00663860, the vtable slot-8 reader named in
	// docs/re/iir-readers-binary-only.md): three u32 reads into obj+0x2C/0x30/0x34, then a std::string into obj+0x48.
	//   [u32 command][u32 targetA][u32 targetB][string argument]
	// If BOTH targetA and targetB read as 0 the client substitutes the pair at obj+8 - i.e. an unspecified target
	// defaults to something the object already carries (self, most likely). The reader then logs the whole thing to
	// its `Fanatic` channel at level 2, which is the cheapest way to see the client's own view of what it sent
	// (enable it with `--debug-channels Fanatic`).
	//
	// ⚠️ PARSE ONLY, deliberately. What the server is supposed to send BACK is not decoded: no capture in the corpus
	// contains a FanaticCommandIIR (the retail traces are ordinary play, not GM sessions), so any reply would be
	// invented. Parsing it at least turns "Unknown 0x2000 Opcode: 0xEE4AA85C" into the actual command the client
	// asked for, which is what a reply has to be built from. Open work: Gitea #448/#449.
	public static class FanaticCommandCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FanaticCommandIir;

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

			if (s.Position + 12 > body.Length)
				return null;

			var command = s.ReadUInt32();
			var targetA = s.ReadUInt32();
			var targetB = s.ReadUInt32();

			// The trailing std::string. Our wire strings are [u16 length][latin1] (FaolanStream.ReadString); if the
			// remaining bytes cannot hold one, report the command without an argument rather than guessing.
			var argument = "";
			if (s.Position + 2 <= body.Length)
			{
				try
				{
					argument = s.ReadString();
				}
				catch
				{
					argument = "";
				}
			}

			return new Parsed(handle, command, targetA, targetB, argument);
		}

		public readonly record struct Parsed(uint Handle, uint Command, uint TargetA, uint TargetB, string Argument);
	}
}
