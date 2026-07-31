using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Reads / writes the FA015807 pre-IIR scaffold (SpawnPreIir) byte-exact: the version byte, then the fixed
	// 6 u8 + 53 u32 grid interleaved with the one count-framed sublist, per SpawnPreIir.Layout.
	//
	// A SUB-RECORD codec (#530 §2 rule 5c): composed inside SimpleCharFullUpdateCodec, never sent alone, so it
	// declares no opcode. There used to be TWO of these - `NpcSpawnPreIirCodec.Encode/TryDecode` and
	// `PlayerSpawnPreFrameCodec.Build/TryParse` - walking the same layout table; the NPC one additionally SCANNED
	// the body to find where the scaffold started, because it was decoded out of a whole body rather than composed
	// into one. Reading the body sequentially removes both the scan and the second copy.
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
