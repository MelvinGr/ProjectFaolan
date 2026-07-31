namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// One `PFAccessWindow_` playfield record out of the FullChar snapshot tails (see AccessWindowCatalog).
	//
	// ⚠️ The two `Unknown*` fields below are NOT undecoded-for-lack-of-trying: they are ZERO in all 86 shipped
	// records, and this table is GAME-STATIC - the beach template and the developed NecroL80 snapshot carry the
	// same 172 `PFAccessWindow_` name strings, so no capture can ever vary them. The corpus is exhausted as an
	// evidence source; naming them needs the client-side consumer, not more traffic (Gitea #464). Same for
	// PfAccessWindowChild.Unknown (0 in all 13) and PfAccessWindowChildDetail.DetailUnknown (0 in all 43).
	// They are carried verbatim so the region still round-trips byte-exact.
	public sealed class PfAccessWindowEntry
	{
		public uint PlayfieldId { get; init; }

		public PfAccessWindowChild? Child { get; init; }
		public byte ChildMode { get; init; }

		public byte SectionOrTier { get; init; }
		public int Offset { get; init; }

		public string InternalName { get; init; } = null!;

		public uint Kind { get; init; }

		// 0 in all 86 catalog records - see the class note.
		public uint Unknown { get; init; }

		// 0 in all 86 catalog records - see the class note.
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
