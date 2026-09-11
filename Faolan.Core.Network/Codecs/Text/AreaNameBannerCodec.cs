namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     The GenericDisplayAreaName FIFO-banner text - the top-of-screen zone or area-name reveal.
	/// </summary>
	/// <remarks>
	///     Byte-exact against the captured banners, all of which carry the same token, category and key; only the text id
	///     varies per area, and those ids are per sub-region, sourced from the client's region volumes. A string codec:
	///     <c>FormatFeedbackCodec.Build(instance, FifoBannerTypeFlags, 1, text)</c> frames it. Build-only, since the
	///     client never sends a banner and there is no inbound body to gate a Parse against.
	/// </remarks>
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

	/// <summary>A FIFO-banner payload: <see cref="FormatFeedbackCodec" /> frames it with the banner typeFlags and footer 1.</summary>
	public sealed record AreaNameBanner(uint TextId) : FeedbackPayload
	{
		public override uint TypeFlags => FormatFeedbackCodec.FifoBannerTypeFlags;
		public override uint Footer => 1;
	}
}
