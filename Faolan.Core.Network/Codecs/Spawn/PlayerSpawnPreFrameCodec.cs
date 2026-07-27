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
				{
					stream.WriteUInt32(ListFrame.ForCount(frame.Sublist.Count));
					foreach (var entry in frame.Sublist)
					{
						stream.WriteUInt32(entry.Key).WriteByte(entry.Flag).WriteString(entry.Name).WriteUInt32(ListFrame.ForCount(entry.FeatIds.Count));
						foreach (var id in entry.FeatIds)
							stream.WriteUInt32(id);
					}
				}
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
					if (!TryReadSublist(frame, ref o, result))
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

		private static bool TryReadSublist(byte[] frame, ref int o, PlayerSpawnPreFrame result)
		{
			if (o + 4 > frame.Length || !ListFrame.TryCountOf(frame.ReadU32Be(o), out var count))
				return false;

			o += 4;
			for (var e = 0; e < count; e++)
			{
				if (o + 4 + 1 + 2 > frame.Length)
					return false;

				var entry = new PlayerSpawnPreFrameSublistEntry
				{
					Key = frame.ReadU32Be(o),
					Flag = frame[o + 4]
				};
				var nameLength = frame.ReadU16Be(o + 5);
				o += 4 + 1 + 2;
				if (o + nameLength + 4 > frame.Length)
					return false;

				entry.Name = Encoding.Latin1.GetString(frame, o, nameLength);
				o += nameLength;
				if (!ListFrame.TryCountOf(frame.ReadU32Be(o), out var ids))
					return false;

				o += 4;
				if (o + 4 * (long)ids > frame.Length)
					return false;

				for (var k = 0; k < ids; k++, o += 4)
					entry.FeatIds.Add(frame.ReadU32Be(o));

				result.Sublist.Add(entry);
			}

			return true;
		}
	}
}
