using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Reads and writes the spawn pre-IIR scaffold (<see cref="SpawnPreIir" />) byte-exact: the version byte, then the
	///     fixed grid of 6 u8 and 53 u32 interleaved with the one count-framed sublist, per SpawnPreIir.Layout.
	/// </summary>
	/// <remarks>
	///     A sub-record codec: composed inside <see cref="SimpleCharFullUpdateCodec" /> and never sent alone, so it
	///     declares no opcode. Because the body is read sequentially, nothing here has to scan for where the scaffold
	///     starts.
	/// </remarks>
	internal static class SpawnPreIirCodec
	{
		internal static SpawnPreIir Read(ref SpanReader r)
		{
			var scaffold = new SpawnPreIir
			{
				Version = r.ExpectU8(SpawnPreIir.ExpectedVersion, "pre-IIR scaffold version")
			};

			var bi = 0;
			var wi = 0;
			foreach (var (isByte, isSublist) in SpawnPreIir.Layout)
			{
				if (isSublist)
					SpawnPreFrameSublistCodec.Read(ref r, scaffold.Sublist);
				else if (isByte)
					scaffold.Flags[bi++] = r.ReadU8();
				else
					scaffold.Words[wi++] = r.ReadU32Be();
			}

			return scaffold;
		}

		internal static void Write(FaolanStream stream, SpawnPreIir scaffold)
		{
			stream.WriteByte(scaffold.Version);

			var bi = 0;
			var wi = 0;
			foreach (var (isByte, isSublist) in SpawnPreIir.Layout)
			{
				if (isSublist)
					SpawnPreFrameSublistCodec.Write(stream, scaffold.Sublist);
				else if (isByte)
					stream.WriteByte(scaffold.Flags[bi++]);
				else
					stream.WriteUInt32(scaffold.Words[wi++]);
			}
		}
	}
}
