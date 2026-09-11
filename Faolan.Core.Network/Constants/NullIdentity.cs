namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     The 8-byte null-identity sentinel <c>B2 90 80 5C 29 E6 27 CA</c>: the value the client's identity/ownership
	///     slots carry when there is nothing to point at.
	/// </summary>
	/// <remarks>
	///     It is the wire's "unset", not a live id - byte-identical in every capture, across unrelated subsystems, and
	///     never varying with the character, item, ability or session.
	///     <para>
	///         Every known site writes the sentinel twice in a row, and the sites that carry surrounding context write
	///         it immediately after a block that is also doubled (the ability grant repeats <c>Key1..Key4</c>; the
	///         create-editor record repeats the 16-byte transform). These are therefore slots for two identities, both
	///         unset, in records whose fields come in pairs - not a 16-byte constant, a category word or a timestamp.
	///         <see cref="Pair" /> is that doubled form, so a caller writes what the record actually holds instead of
	///         spelling the value twice.
	///     </para>
	///     <para>
	///         Do not read it as an owner id and fill it in with a real one: no capture has shown a non-sentinel value
	///         in any of these slots, so what a populated identity looks like here is unknown. [V0]
	///     </para>
	/// </remarks>
	public static class NullIdentity
	{
		public const uint High = 0xB290805C;
		public const uint Low = 0x29E627CA;

		// The sentinel as its 8 wire bytes.
		public static readonly byte[] Bytes = [0xB2, 0x90, 0x80, 0x5C, 0x29, 0xE6, 0x27, 0xCA];

		// Two consecutive unset identities - the form every known site writes.
		public static readonly uint[] Pair = [High, Low, High, Low];
	}
}
