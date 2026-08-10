namespace Faolan.Core.Network.Codecs.Text
{
	// The `FeatRequired` on-screen feedback TEXT - the `remoteformat` a FormatFeedbackIIR (0x8D5A8B6B) carries when
	// retail REFUSES an action because the character has not trained a feat. It does not say "you cannot do that": it
	// names the missing feat, and the client renders that name from its own string table.
	//
	// Captured refusing a HARVEST (#779): a Stonecutting Apprentice swung at a Marble node and got this back, naming
	// feat template 894305 = `Stonecutting Adept` - exactly the rung the player's own chat annotation says they were
	// missing ("tried to mine marbel. but need Adept stonecutting").
	// [V2: demol80-demonologist_login_gameplay_annotated_1.tshark.json fr5644/fr5726]
	//
	// A rule-1a STRING codec (#530): the payload is text sitting inside a byte codec, and
	// FormatFeedbackCodec.Build(instance, payload) frames it. Build-only, like AttributeChangedCodec - the client
	// never sends this, so there is no captured inbound body a Parse could be gated against.
	//
	// ⭐ It generalises to ANY feat, and that is a finding rather than an assumption. Both `key` attributes are LDB
	// DECRYPTION keys (#520), so substituting a different feat id would normally need that string's own key - but
	// category 50220 is PLAINTEXT (every one of its seven shipped languages resolves through `--report ldb-search
	// --report-arg id:894305` with no key at all), so the key is inert here and the captured nonces are reusable
	// verbatim. That is the same conclusion AttributeChangedCodec reached for its three keys, and it is what makes
	// this a codec instead of a `FeedbackXml` literal.
	public static class FeatRequiredCodec
	{
		// The LDB category the missing feat's NAME is resolved through - the client's feat-name string table.
		public const uint FeatNameCategory = 50220;

		// The captured key nonces, already entity-escaped exactly as they appear on the wire.
		public const string DefaultOuterKey = "Sb!&amp;U1?)arPM1B&amp;*4,vG";
		public const string DefaultFeatKey = "@nH]c)u[%1Jqk,UbTNHs";

		public static string Build(FeatRequired value)
		{
			return $"<remoteformat token=\"FeatRequired\" category=\"100\" key=\"{value.OuterKey}\" knubot=\"0\"  >" +
			       $"<parameter value=\"7;&lt;remoteformat id=&quot;{value.FeatTemplateId}&quot; category=&quot;{FeatNameCategory}&quot; key=&quot;{value.FeatKey}&quot; knubot=&quot;0&quot;  &gt;&lt;/remoteformat&gt;\" />" +
			       "</remoteformat>";
		}

		// With the reusable captured key nonces.
		public static string Build(uint featTemplateId)
		{
			return Build(new FeatRequired(featTemplateId));
		}
	}

	// "You need <feat>" - the feat is named by its RDB TEMPLATE id (the id space `Feats.Id` uses), not by the wire
	// feat id the character's own feat list speaks. Retail frames it as an ordinary FIFO feedback: typeFlags 0,
	// footer 1, which are FeedbackPayload's banner defaults.
	public sealed record FeatRequired(
		uint FeatTemplateId,
		string OuterKey = FeatRequiredCodec.DefaultOuterKey,
		string FeatKey = FeatRequiredCodec.DefaultFeatKey) : FeedbackPayload
	{
		public override uint TypeFlags => FormatFeedbackCodec.FifoBannerTypeFlags;
		public override uint Footer => 1;
	}
}
