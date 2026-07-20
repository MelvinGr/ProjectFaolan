using System.Text;

namespace Faolan.Core.Network.Codecs.Text
{
	// Builds the AttributeChanged / Increased on-screen feedback - a FormatFeedbackIIR (0x8D5A8B6B) payload shown
	// when a character's primary attribute rises: the VISIBLE half of a level-up (and of a quest attribute reward).
	// Retail sends ONE such message per attribute that went up, then a 0x96B8DC59 vitals push with the new maxima.
	// There is no "You reached level N" banner in any capture - this attribute burst is the level-up feedback (#209).
	//
	// Grammar decoded in tools/capture/decode_levelup_feedback.py, golden-tested in AttributeChangedFeedbackTests.
	// The XML is entity-escaped (&quot;=" &lt;/&gt;=</> ). The three key="..." values are random per-burst nonces the
	// client does not validate - reuse is in-client proven (the GenericDisplayAreaName banner reuses one captured
	// key), so the live builder reuses fixed captured keys.
	public static class AttributeChangedFeedback
	{
		public const uint Footer = 0;

		// Invariant across all 33 captured AttributeChanged messages (decode_levelup_feedback.py).
		public const uint TypeFlags = 0x40000006;
		private const string DefaultAttributeKey = "?m3qJZNrTSBky8rp2l,w";
		private const string DefaultIncreasedKey = "akfLdN&amp;#39;`0h^/eax0GI?K";

		// Captured per-burst key nonces (from the strickenbla level-up burst). Reused verbatim by the live builder;
		// already entity-escaped exactly as they appear on the wire.
		private const string DefaultOuterKey = "W8Kfv/@l29p&#36;ea0.&#36;r?.";

		// The 5 primary-attribute category-615 localized string ids (stable across every capture). Every class gains
		// all five on a level-up; only the amounts differ. The wire id is authoritative - we do not claim a
		// id->Strength/Dexterity/... name mapping (not recoverable from the captures).
		public static readonly uint[] PrimaryAttributeIds = [804, 806, 808, 810, 814];

		// Builds the full FormatFeedbackIIR body for an attribute-increase message addressed to the player's
		// GameInterface instance, reusing the captured key nonces.
		public static byte[] Build(uint instance, uint attributeId, uint amount)
		{
			return FormatFeedbackCodec.Build(instance, TypeFlags, Footer, Encoding.Latin1.GetBytes(BuildXml(attributeId, amount)));
		}

		// Assembles the AttributeChanged remoteformat XML for one attribute. The key arguments are passed through
		// verbatim (already wire-escaped); attributeId is a category-615 primary-attribute id and amount is how much
		// it rose. Kept byte-faithful to the captured template (whitespace and escaping included).
		public static string BuildXml(uint attributeId, uint amount, string outerKey, string attributeKey, string increasedKey)
		{
			return $"<remoteformat token=\"AttributeChanged\" category=\"100\" key=\"{outerKey}\" knubot=\"0\"  >" +
			       $"<parameter value=\"7;&lt;remoteformat id=&quot;{attributeId}&quot; category=&quot;615&quot; key=&quot;{attributeKey}&quot; knubot=&quot;0&quot;  &gt;&lt;/remoteformat&gt;\" />" +
			       $"<parameter value=\"7;&lt;remoteformat token=&quot;Increased&quot; category=&quot;100&quot; key=&quot;{increasedKey}&quot; knubot=&quot;0&quot;  &gt;&lt;/remoteformat&gt;\" />" +
			       $"<parameter value=\"1;{amount}\" /></remoteformat>";
		}

		// As BuildXml above but with the reusable captured key nonces (the client does not validate them).
		public static string BuildXml(uint attributeId, uint amount)
		{
			return BuildXml(attributeId, amount, DefaultOuterKey, DefaultAttributeKey, DefaultIncreasedKey);
		}
	}
}
