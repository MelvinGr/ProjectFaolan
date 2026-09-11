namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     The AttributeChanged / Increased on-screen feedback text - the <c>remoteformat</c> a FormatFeedbackIIR carries
	///     when a character's primary attribute rises: the visible half of a level-up, and of a quest attribute reward.
	/// </summary>
	/// <remarks>
	///     Retail sends one such message per attribute that went up, then a vitals push with the new maxima. There is no
	///     "You reached level N" banner in any capture - this attribute burst is the level-up feedback.
	///     <para>
	///         A string codec: the payload is text sitting inside a byte codec, so <c>Build</c> composes a string and
	///         <c>FormatFeedbackCodec.Build(instance, TypeFlags, Footer, text)</c> frames it. Build-only, since the client
	///         never sends this, so there is no captured inbound body a Parse could be gated against and an ungated regex
	///         inverse would be a decode claim nothing checks.
	///     </para>
	///     <para>
	///         Grammar decoded in tools/capture/decode_levelup_feedback.py, golden-tested in
	///         <c>AttributeChangedCodecTests</c>. The XML is entity-escaped. The three <c>key</c> values are random
	///         per-burst nonces the client does not validate - reuse is proven in-client, the GenericDisplayAreaName
	///         banner reusing one captured key - so the defaults below are fixed captured ones.
	///     </para>
	/// </remarks>
	public static class AttributeChangedCodec
	{
		public const uint Footer = 0;

		// Invariant across all 33 captured AttributeChanged messages (decode_levelup_feedback.py).
		public const uint TypeFlags = 0x40000006;

		// Captured per-burst key nonces (from the strickenbla level-up burst). Reused verbatim by the live builder; already
		// entity-escaped exactly as they appear on the wire.
		public const string DefaultAttributeKey = "?m3qJZNrTSBky8rp2l,w";
		public const string DefaultIncreasedKey = "akfLdN&amp;#39;`0h^/eax0GI?K";
		public const string DefaultOuterKey = "W8Kfv/@l29p&#36;ea0.&#36;r?.";

		// The five primary-attribute category-615 localized string ids, stable across every capture. Every class gains all
		// five on a level-up and only the amounts differ. The wire id is authoritative: no id-to-name mapping is claimed,
		// since none is recoverable from the captures.
		public static readonly uint[] PrimaryAttributeIds = [804, 806, 808, 810, 814];

		// Attribute order and spacing are part of the bytes rather than cosmetic: the keys are passed through verbatim,
		// already wire-escaped, and the amount rides the last parameter as a `1;<n>` scalar.
		public static string Build(AttributeChanged value)
		{
			return $"<remoteformat token=\"AttributeChanged\" category=\"100\" key=\"{value.OuterKey}\" knubot=\"0\"  >" +
			       $"<parameter value=\"7;&lt;remoteformat id=&quot;{value.AttributeId}&quot; category=&quot;615&quot; key=&quot;{value.AttributeKey}&quot; knubot=&quot;0&quot;  &gt;&lt;/remoteformat&gt;\" />" +
			       $"<parameter value=\"7;&lt;remoteformat token=&quot;Increased&quot; category=&quot;100&quot; key=&quot;{value.IncreasedKey}&quot; knubot=&quot;0&quot;  &gt;&lt;/remoteformat&gt;\" />" +
			       $"<parameter value=\"1;{value.Amount}\" /></remoteformat>";
		}

		// With the reusable captured key nonces; the client does not validate them.
		public static string Build(uint attributeId, uint amount)
		{
			return Build(new AttributeChanged(attributeId, amount));
		}
	}

	/// <summary>
	///     An effect-line payload rather than a banner: retail frames it with typeFlags 0x40000006 and footer 0.
	/// </summary>
	public sealed record AttributeChanged(
		uint AttributeId,
		uint Amount,
		string OuterKey = AttributeChangedCodec.DefaultOuterKey,
		string AttributeKey = AttributeChangedCodec.DefaultAttributeKey,
		string IncreasedKey = AttributeChangedCodec.DefaultIncreasedKey) : FeedbackPayload
	{
		public override uint TypeFlags => AttributeChangedCodec.TypeFlags;
		public override uint Footer => AttributeChangedCodec.Footer;
	}
}
