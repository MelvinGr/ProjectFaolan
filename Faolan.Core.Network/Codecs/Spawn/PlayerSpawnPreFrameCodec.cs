using System.Diagnostics.CodeAnalysis;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Parses / rebuilds PlayerSpawnPreFrame byte-exact. The field grid comes from NpcSpawnPreIir.Layout - the same
	// table the NPC scaffold walks, because it is the same client reader (FUN_01121c00); the only difference is
	// that the player's sublist is non-empty and the frame carries 5 trailing bytes of the embedded CoreCharacter
	// header (see PlayerSpawnPreFrame).
	public static class PlayerSpawnPreFrameCodec
	{
		public static byte[] Build(PlayerSpawnPreFrame frame)
		{
			var stream = new FaolanStream().WriteByte(frame.Version);
			var bi = 0;
			var wi = 0;
			foreach (var (isByte, isSublist) in NpcSpawnPreIir.Layout)
			{
				if (isSublist)
					SpawnPreFrameSublist.Write(stream, frame.Sublist);
				else if (isByte)
					stream.WriteByte(frame.Flags[bi++]);
				else
					stream.WriteUInt32(frame.Words[wi++]);
			}

			return stream.WriteByte(frame.CoreCharVersion).WriteUInt32(frame.CoreCharIirHeader).ToArray();
		}

		// Parses a player pre-IIR frame. Returns false when the walk does not consume the frame EXACTLY - which is
		// the whole point of the gate: a frame that framed "close enough" would be a mis-decode, and a tolerant
		// reader here is what let the sublist read as three magic constants for so long.
		public static bool TryParse(byte[] frame, [NotNullWhen(true)] out PlayerSpawnPreFrame? parsed)
		{
			parsed = null;
			if (frame == null || frame.Length < 1 || frame[0] != NpcSpawnPreIir.ExpectedVersion)
				return false;

			var result = new PlayerSpawnPreFrame
			{
				Version = frame[0]
			};
			var o = 1;
			var bi = 0;
			var wi = 0;
			foreach (var (isByte, isSublist) in NpcSpawnPreIir.Layout)
			{
				if (isSublist)
				{
					if (!SpawnPreFrameSublist.TryRead(frame, ref o, result.Sublist))
						return false;
				}
				else if (isByte)
				{
					if (o + 1 > frame.Length)
						return false;

					result.Flags[bi++] = frame[o++];
				}
				else
				{
					if (o + 4 > frame.Length)
						return false;

					result.Words[wi++] = frame.ReadU32Be(o);
					o += 4;
				}
			}

			if (o + 5 != frame.Length || frame[o] != PlayerSpawnPreFrame.CoreCharacterVersion)
				return false;

			result.CoreCharVersion = frame[o];
			result.CoreCharIirHeader = frame.ReadU32Be(o + 1);
			parsed = result;
			return true;
		}
	}
}
