namespace Faolan.Core.Network.Codecs.Text
{
	// The GenericDisplayAreaName FIFO-banner TEXT - the top-of-screen zone/area-name reveal. Byte-exact against the
	// captured banners (all carry this same token/category/key; only the text id varies per area - the ids are per
	// SUB-REGION, sourced from the client's region volumes, #348).
	//
	// A rule-1a STRING codec (#530); FormatFeedbackCodec.Build(instance, FifoBannerTypeFlags, 1, text) frames it.
	// Build-only: the client never sends a banner, so there is no inbound body to gate a Parse against.
	public static class AreaNameBannerCodec
	{
		public static string Build(AreaNameBanner value)
		{
			return "<remoteformat token=\"showfifomessagefromserver(8)|main(1)|GenericDisplayAreaName\" category=\"50018\" " +
			       "key=\"pcAdIWYhSXfTFDe5I1g7\" knubot=\"0\"  ><parameter value=\"7;&lt;localized category=&#39;50025&#39; " +
			       $"token=&#39;DisplayedText(3)|GenericDisplayAreaName|{value.TextId}&#39;&gt;\" name=\"DisplayedText\" /></remoteformat>";
		}

		public static string Build(uint textId)
		{
			return Build(new AreaNameBanner(textId));
		}
	}

	// A FIFO-banner payload: FormatFeedbackCodec frames it with the banner typeFlags and footer 1.
	public sealed record AreaNameBanner(uint TextId) : FeedbackPayload
	{
		public override uint TypeFlags => FormatFeedbackCodec.FifoBannerTypeFlags;
		public override uint Footer => 1;
	}
}
