namespace Faolan.Core.Network.Codecs.Text
{
	// The "resurrection point discovered" FIFO banner TEXT (#372) - retail's toast when a player walks near a point
	// they had not found yet. Sent alongside the DispatchValidResurrectionSpots re-send that adds the point to the
	// player's list (ResurrectionSpotsBody); the banner is what the player actually SEES. Framed by
	// FormatFeedbackCodec.Build(instance, FifoBannerTypeFlags, 1, text) - same framing as the area-name banner.
	//
	// This message identifies its string by `id=` rather than the `token=` the other feedback XML uses, which is
	// why a scan for `remoteformat token="..."` finds no discovery message and wrongly suggests none exists (#372
	// was stuck on exactly that). The parameter is a NESTED `<localized>` naming the point by its own id under
	// category 270 - the LDB category holding the point names ("Water's Edge", "Secluded Cove", ...). So no name
	// ever crosses the wire; the client resolves it.
	//
	// `NameKey` is the per-POINT key of that category-270 string: constant per point, different between points
	// (784237 and 784238 have different ones), unlike the outer key which is fixed per message id. Pass the
	// capture-verbatim ESCAPED form. Null omits the attribute - the area-name banner's nested `<localized>` has no
	// key at all and renders, so it is not obviously required for every string.
	//
	// A rule-1a STRING codec (#530), build-only: the client never sends this.
	public static class RespawnPointDiscoveredCodec
	{
		public static string Build(RespawnPointDiscovered value)
		{
			var key = value.NameKey == null
				? ""
				: $"key=&quot;{value.NameKey}&quot; ";

			return "<remoteformat id=\"218327236\" category=\"100\" key=\"KsATGOK`H%9l@@z[1mo,\" knubot=\"0\"  >" +
			       $"<parameter value=\"7;&lt;localized id=&quot;{value.PointId}&quot; category=&quot;270&quot; " + $"flags=&quot;&quot; {key}&gt;\" /></remoteformat>";
		}

		public static string Build(uint pointId, string? nameKey)
		{
			return Build(new RespawnPointDiscovered(pointId, nameKey));
		}
	}

	// A FIFO-banner payload - the same framing as the area-name banner.
	public sealed record RespawnPointDiscovered(uint PointId, string? NameKey) : FeedbackPayload
	{
		public override uint TypeFlags => FormatFeedbackCodec.FifoBannerTypeFlags;
		public override uint Footer => 1;
	}
}
