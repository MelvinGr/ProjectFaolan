namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     The <c>FeatRequired</c> on-screen feedback text - the <c>remoteformat</c> a FormatFeedbackIIR carries when
	///     retail refuses an action because the character has not trained a feat. It does not say "you cannot do that": it
	///     names the missing feat, and the client renders that name from its own string table.
	/// </summary>
	/// <remarks>
	///     Captured refusing a harvest: a Stonecutting Apprentice swung at a Marble node and got this back, naming feat
	///     template 894305 = <c>Stonecutting Adept</c>, exactly the rung the player's own chat annotation says they were
	///     missing. [V2: demol80-demonologist_login_gameplay_annotated_1.tshark.json fr5644/fr5726]
	///     <para>
	///         A string codec: the payload is text sitting inside a byte codec, and
	///         <c>FormatFeedbackCodec.Build(instance, payload)</c> frames it. Build-only, like
	///         <see cref="AttributeChangedCodec" />, since the client never sends this.
	///     </para>
	///     <para>
	///         It generalises to any feat. Both <c>key</c> attributes are LDB decryption keys, so substituting a different
	///         feat id would normally need that string's own key - but category 50220 is plaintext (every one of its seven
	///         shipped languages resolves through <c>--report ldb-search --report-arg id:894305</c> with no key at all),
	///         so the key is inert here and the captured nonces are reusable verbatim.
	///     </para>
	/// </remarks>
	public static class FeatRequiredCodec
	{
		// The LDB category the missing feat's name is resolved through - the client's feat-name string table.
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

	/// <summary>
	///     "You need &lt;feat&gt;" - the feat is named by its RDB template id (the id space <c>Feats.Id</c> uses) rather
	///     than by the wire feat id the character's own feat list speaks. Retail frames it as an ordinary FIFO feedback:
	///     typeFlags 0, footer 1, which are <c>FeedbackPayload</c>'s banner defaults.
	/// </summary>
	public sealed record FeatRequired(
		uint FeatTemplateId,
		string OuterKey = FeatRequiredCodec.DefaultOuterKey,
		string FeatKey = FeatRequiredCodec.DefaultFeatKey) : FeedbackPayload
	{
		public override uint TypeFlags => FormatFeedbackCodec.FifoBannerTypeFlags;
		public override uint Footer => 1;
	}
}
