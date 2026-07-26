using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	public sealed class PfAccessWindowEntry
	{
		public uint PlayfieldId { get; init; }

		public PfAccessWindowChild? Child { get; init; }
		public byte ChildMode { get; init; }

		public byte SectionOrTier { get; init; }
		public int Offset { get; init; }

		public string InternalName { get; init; } = null!;

		public uint Kind { get; init; }

		public uint Unknown { get; init; }

		public ushort UnknownBeforeChildMode { get; init; }

		public override string ToString()
		{
			var child = Child == null
				? ""
				: $" | child: {Child}";

			return $"0x{Offset:X8}: {PlayfieldId} {InternalName} section={SectionOrTier} kind={Kind} childMode={ChildMode}{child}";
		}
	}
}
