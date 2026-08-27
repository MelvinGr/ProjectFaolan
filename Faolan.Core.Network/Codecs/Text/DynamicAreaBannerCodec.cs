using System.Security;

namespace Faolan.Core.Network.Codecs.Text
{
	// The top-of-screen FIFO banner (the "toast") carrying ARBITRARY server-authored text - not a pre-defined LDB
	// string. Reverse-engineered 2026-08-27 (Gitea #664, which had been closed as "not possible"):
	//
	//   - The GenericDisplayAreaName FIFO template (LDB category 50018) resolves to the format body `{DisplayedText}`
	//     - i.e. it renders its `DisplayedText` parameter VERBATIM, the client-side equivalent of a bare `%s`.
	//   - A remoteformat `<parameter>` of wire-type 7 is a STRING that the client substitutes as-is UNLESS its text
	//     is itself a nested `<localized>`/`<remoteformat>` tag (which it then resolves recursively). The area-name
	//     banner nests a `<localized>`; but the corpus also carries type-7 parameters holding plain LITERAL text -
	//     e.g. `TradePostGUI_MailItemBought` fills its `%s` with `value="7;AssassinL23"` (a real player name, never an
	//     LDB entry). So a literal string in a type-7 parameter renders exactly as written.
	//
	// Put together: the same GenericDisplayAreaName envelope AreaNameBannerCodec builds, but with the nested
	// `<localized>` replaced by the literal text, makes the top banner show whatever the server says. This is what
	// AreaNameBannerCodec cannot do - it can only name a client-known area id.
	//
	// A rule-1a STRING codec (#530); FormatFeedbackCodec.Build(instance, FifoBannerTypeFlags, 1, text) frames it.
	// Build-only: the client never sends a banner, so there is no inbound body to gate a Parse against.
	public static class DynamicAreaBannerCodec
	{
		// The outer envelope is the exact area-name FIFO template, key and all - the client resolves the template out
		// of its own LDB and does not validate the wire key (AreaNameBannerCodec ships the same constant). Only the
		// parameter differs: a literal type-7 string in place of the nested <localized>.
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

	// A FIFO-banner payload carrying arbitrary literal text: FormatFeedbackCodec frames it with the banner typeFlags
	// and footer 1, the same as the area-name banner it is modelled on.
	public sealed record DynamicAreaBanner(string Text) : FeedbackPayload
	{
		public override uint TypeFlags => FormatFeedbackCodec.FifoBannerTypeFlags;
		public override uint Footer => 1;
	}
}
