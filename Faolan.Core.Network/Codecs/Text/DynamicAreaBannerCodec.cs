using System.Security;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     The top-of-screen FIFO banner (the "toast") carrying arbitrary server-authored text rather than a pre-defined
	///     LDB string.
	/// </summary>
	/// <remarks>
	///     Two client behaviours make it possible. The GenericDisplayAreaName FIFO template (LDB category 50018) resolves
	///     to the format body <c>{DisplayedText}</c>, i.e. it renders its <c>DisplayedText</c> parameter verbatim, the
	///     client-side equivalent of a bare <c>%s</c>. And a remoteformat <c>parameter</c> of wire-type 7 is a string the
	///     client substitutes as-is unless its text is itself a nested <c>localized</c> or <c>remoteformat</c> tag, which
	///     it then resolves recursively - the area-name banner nests a <c>localized</c>, while the corpus also carries
	///     type-7 parameters holding plain literal text, such as <c>TradePostGUI_MailItemBought</c> filling its
	///     <c>%s</c> with a real player name that is never an LDB entry.
	///     <para>
	///         Put together: the same GenericDisplayAreaName envelope <see cref="AreaNameBannerCodec" /> builds, with the
	///         nested <c>localized</c> replaced by the literal text, makes the top banner show whatever the server says.
	///         That is what <see cref="AreaNameBannerCodec" /> cannot do, since it can only name a client-known area id.
	///     </para>
	///     <para>
	///         A string codec: <c>FormatFeedbackCodec.Build(instance, FifoBannerTypeFlags, 1, text)</c> frames it.
	///         Build-only, since the client never sends a banner.
	///     </para>
	/// </remarks>
	public static class DynamicAreaBannerCodec
	{
		// The outer envelope is the exact area-name FIFO template, key and all - the client resolves the template out of its
		// own LDB and does not validate the wire key, and AreaNameBannerCodec ships the same constant. Only the parameter
		// differs: a literal type-7 string in place of the nested <localized>.
		public static string Build(DynamicAreaBanner value)
		{
			var text = SecurityElement.Escape(value.Text) ?? "";

			return "<remoteformat token=\"showfifomessagefromserver(8)|main(1)|GenericDisplayAreaName\" category=\"50018\" " +
			       "key=\"pcAdIWYhSXfTFDe5I1g7\" knubot=\"0\"  >" +
			       $"<parameter value=\"7;{text}\" name=\"DisplayedText\" /></remoteformat>";
		}

		public static string Build(string text)
		{
			return Build(new DynamicAreaBanner(text));
		}
	}

	/// <summary>
	///     A FIFO-banner payload carrying arbitrary literal text: <see cref="FormatFeedbackCodec" /> frames it with the
	///     banner typeFlags and footer 1, the same as the area-name banner it is modelled on.
	/// </summary>
	public sealed record DynamicAreaBanner(string Text) : FeedbackPayload
	{
		public override uint TypeFlags => FormatFeedbackCodec.FifoBannerTypeFlags;
		public override uint Footer => 1;
	}
}
