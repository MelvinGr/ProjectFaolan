using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	public sealed class PfAccessWindowChild
	{
		public List<PfAccessWindowChildDetail> Details { get; init; } = [];
		public bool HasDetails => Details.Count != 0;
		public byte Variant { get; init; }

		public byte? DetailsMode { get; init; }

		public uint? Id { get; init; }

		// 0 in all 13 catalog children that carry it - see the note on PfAccessWindowEntry (Gitea #464).
		public uint? Unknown { get; init; }

		public override string ToString()
		{
			if (!HasDetails)
			{
				return Id == null
					? $"variant={Variant}"
					: $"variant={Variant}, id=0x{Id:X8} ({Id})";
			}

			var id = Id == null
				? ""
				: $"id=0x{Id:X8} ({Id}), ";

			if (Details.Count == 1)
				return $"variant={Variant}, {id}{Details[0]}";

			return $"variant={Variant}, {id}details={Details.Count}";
		}
	}
}
