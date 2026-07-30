namespace Faolan.Core.Network.Codecs.Text
{
	// The AttributeChanged / Increased on-screen feedback TEXT - the `remoteformat` a FormatFeedbackIIR (0x8D5A8B6B)
	// carries when a character's primary attribute rises: the VISIBLE half of a level-up (and of a quest attribute
	// reward). Retail sends ONE such message per attribute that went up, then a 0x96B8DC59 vitals push with the new
	// maxima. There is no "You reached level N" banner in any capture - this attribute burst IS the level-up
	// feedback (#209).
	//
	// A rule-1a STRING codec (#530): the payload is text sitting inside a byte codec, so Build composes a `string`
	// and FormatFeedbackCodec.Build(instance, TypeFlags, Footer, text) frames it. Build-only - the client never
	// sends this, so there is no captured inbound body a Parse could be gated against, and an ungated regex inverse
	// would be a decode claim nothing checks.
	//
	// Grammar decoded in tools/capture/decode_levelup_feedback.py, golden-tested in AttributeChangedCodecTests.
	// The XML is entity-escaped (&quot;=" &lt;/&gt;=</> ). The three key="..." values are random per-burst nonces the
	// client does not validate - reuse is in-client proven (the GenericDisplayAreaName banner reuses one captured
	// key), so the default keys below are fixed captured ones.
	public static class AttributeChangedCodec
	{
		public const uint Footer = 0;

		// Invariant across all 33 captured AttributeChanged messages (decode_levelup_feedback.py).
		public const uint TypeFlags = 0x40000006;

		// Captured per-burst key nonces (from the strickenbla level-up burst). Reused verbatim by the live builder;
		// already entity-escaped exactly as they appear on the wire.
		public const string DefaultAttributeKey = "?m3qJZNrTSBky8rp2l,w";
		public const string DefaultIncreasedKey = "akfLdN&amp;#39;`0h^/eax0GI?K";
		public const string DefaultOuterKey = "W8Kfv/@l29p&#36;ea0.&#36;r?.";

		// The 5 primary-attribute category-615 localized string ids (stable across every capture). Every class gains
		// all five on a level-up; only the amounts differ. The wire id is authoritative - we do not claim a
		// id->Strength/Dexterity/... name mapping (not recoverable from the captures).
		public static readonly uint[] PrimaryAttributeIds = [804, 806, 808, 810, 814];

		// Attribute ORDER and spacing are part of the bytes, not cosmetic - the keys are passed through verbatim
		// (already wire-escaped) and the amount rides the last parameter as a `1;<n>` scalar.
		public static string Build(AttributeChanged value)
		{
			return $"<remoteformat token=\"AttributeChanged\" category=\"100\" key=\"{value.OuterKey}\" knubot=\"0\"  >" +
			       $"<parameter value=\"7;&lt;remoteformat id=&quot;{value.AttributeId}&quot; category=&quot;615&quot; key=&quot;{value.AttributeKey}&quot; knubot=&quot;0&quot;  &gt;&lt;/remoteformat&gt;\" />" +
			       $"<parameter value=\"7;&lt;remoteformat token=&quot;Increased&quot; category=&quot;100&quot; key=&quot;{value.IncreasedKey}&quot; knubot=&quot;0&quot;  &gt;&lt;/remoteformat&gt;\" />" +
			       $"<parameter value=\"1;{value.Amount}\" /></remoteformat>";
		}

		// With the reusable captured key nonces (the client does not validate them).
		public static string Build(uint attributeId, uint amount)
		{
			return Build(new AttributeChanged(attributeId, amount));
		}
	}

	public sealed record AttributeChanged(
		uint AttributeId,
		uint Amount,
		string OuterKey = AttributeChangedCodec.DefaultOuterKey,
		string AttributeKey = AttributeChangedCodec.DefaultAttributeKey,
		string IncreasedKey = AttributeChangedCodec.DefaultIncreasedKey);
}
