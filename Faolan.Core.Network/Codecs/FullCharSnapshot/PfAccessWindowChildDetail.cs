namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	public sealed class PfAccessWindowChildDetail
	{
		// 0 in all 43 catalog details - see the note on PfAccessWindowEntry (Gitea #464).
		public byte DetailUnknown { get; init; }
		public byte DetailsMode { get; init; }

		public string DisplayName { get; init; } = null!;

		public string ParentName { get; init; } = null!;

		public string ShortCode { get; init; } = null!;

		public override string ToString()
		{
			return $"mode={DetailsMode}, code={ShortCode}, display={DisplayName}, parent={ParentName}";
		}
	}
}
