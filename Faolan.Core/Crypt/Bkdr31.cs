namespace Faolan.Core.Crypt
{
	// The client's string hash: `h = h * 31 + c`, unsigned 32-bit, i.e. Java's `String.hashCode`. Recovered by
	// RE'ing AgeOfConan.exe (docs/re/aoc-opcode-ids.md), and it is what turns a name into an id in two id namespaces:
	//
	//   * the RPC endpoint class ids - `BKDR-31("GameCharAgent") == 0xA9ECB95D` (`Constants.RpcInterfaces`);
	//   * the 32-bit IIR type sub-opcodes carried inside a PassBlob - `BKDR-31("FeatIIR") == 0x28DA68A0`.
	//
	// One function, in Crypt, because it is a hash and a hash re-implemented per caller is a hash that can disagree
	// with itself. The Python copy in `Faolan.Wireshark/scripts/gen_opcode_names.py` is the one exception, since
	// that script runs outside the solution; it asserts its results against the same expected ids.
	//
	// Unsigned and unchecked. The client's is a 32-bit wrap; a checked context would throw partway through most
	// real names, and a signed one would disagree with every id in the catalogs.
	public static class Bkdr31
	{
		public static uint Of(string value)
		{
			uint h = 0;
			foreach (var c in value)
				h = unchecked(h * 31 + c);

			return h;
		}
	}
}
