namespace Faolan.Core.Network.Codecs
{
	// A self-serializing wire VALUE: a decoded message (record/class/struct) that knows its own byte layout, so
	// Write(stream) and the static Read(stream) are a matched pair - Read(stream over x.ToArray()) == x.
	//
	// This is deliberately NOT the contract for the ~40 static `*Codec`/`*Builder` module classes. A C# static class
	// cannot implement an interface, and those codecs are stateless operations over domain inputs (Build(instance,
	// xml), Parse(record), ...) rather than instances that serialize themselves. Their uniform contract is the
	// `Opcode` / `HandledTypes` constant instead, enforced by CodecConventionTests. Trying to make one interface
	// span both shapes is what motivated #317 - see docs/protocol/methodology.md.
	//
	// NAMING follows that split, and CodecConventionTests.TypesImplementingICodec_AreNotNamedCodec enforces it: the
	// `*Codec`/`*Builder` suffix belongs to the static MODULE, so a type implementing this interface is named after
	// the VALUE it holds (ChainKill, Regen, GuildTagBody, NpcSpellEffect). A value may also live nested inside its
	// module when the module has other ops to offer - CombatShieldCodec.Message, StatDamageCodec.Message. What it
	// must not be is `ChainKillCodec.Parse(body)` returning a `ChainKillCodec`: a codec that is a value.
	public interface ICodec<out T>
	{
		void Write(FaolanStream s);
		static abstract T Read(FaolanStream stream);
	}

	public static class CodecExtensions
	{
		// Produce a fresh buffer for a wire value; composing into a larger body uses Write(stream) directly.
		public static byte[] ToArray<T>(this ICodec<T> codec)
		{
			using var s = new FaolanStream();
			codec.Write(s);
			return s.ToArray();
		}
	}
}
