using System.Text;

namespace Faolan.Core.Network.Codecs.Economy
{
	// Builds the remoteformat XML body of the auction-house "you bought an item" mail
	// (DispatchMailMessage, token TradePostGUI_MailItemBought). Decoded byte-exact from the necrol80
	// buy_from_autionhouse_get_mail capture (the 576-byte XML inside the 629-byte mail #1604/#1611;
	// docs/tradepost-protocol.md). The captured body:
	//
	//   <remoteformat token="TradePostGUI_MailItemBought" category="629" key="..." knubot="0"  >
	//     <parameter value="7;<buyer>" />
	//     <parameter value="1;<itemInstanceId decimal>" />
	//     <parameter value="7;<item-name remoteformat, XML-escaped>" />
	//     <parameter value="7;<seller>" />
	//     <parameter value="7;<price> &lt;localized token=&quot;CurrencyTinSmall&quot; ... &gt;" />
	//   </remoteformat>
	//
	// The outer message key and the currency-localized fragment are RDB localization constants for THIS message
	// type (fixed across buys), so they are baked in; the substitutable fields are the buyer, the bought item's
	// instance id (= the saleId), the item-name remoteformat, the seller, and the price text. The item-name
	// remoteformat carries a per-item RDB string id + key we cannot synthesize for an arbitrary item (the same
	// client-RDB-by-id limit as conversation/quest text), so live buys pass a minimal name fragment; the captured
	// fragment round-trips byte-exact for the golden test.
	public static class TradepostMailBody
	{
		// The captured item-name remoteformat (item 163118), already XML-escaped for embedding in a parameter value
		// (note the double-escaped &amp;amp; in its key). The golden-test oracle and a sane default identity.
		public const string CapturedItemNameEscaped =
			"&lt;remoteformat id=&quot;163118&quot; category=&quot;50200&quot; key=&quot;)tCcAdyb]L&amp;amp;Mn]6d[9EV&quot; knubot=&quot;0&quot;  &gt;&lt;/remoteformat&gt;";

		// The price suffix: a leading space then the CurrencyTinSmall localized token (fixed RDB key, category 500),
		// already in the XML-escaped form it takes inside a parameter value. Appended after the price number.
		private const string CurrencySuffix = " &lt;localized token=&quot;CurrencyTinSmall&quot; category=&quot;500&quot; flags=&quot;&quot; key=&quot;[E%w*HpIhNL!E&amp;#92;g-kr)c&quot; &gt;";

		private const string OuterClose = "</remoteformat>";

		// Outer message open/close - the TradePostGUI_MailItemBought template's fixed RDB key (category 629). The
		// two trailing spaces before '>' are reproduced from the capture (the client emits them verbatim).
		private const string OuterOpen = "<remoteformat token=\"TradePostGUI_MailItemBought\" category=\"629\" key=\"#x[c`Nt]6eQ!6I]Tj]Lw\" knubot=\"0\"  >";

		// Builds the full mail XML. `itemNameEscaped` is the item-name remoteformat already escaped for a parameter
		// value (use BuildItemNameEscaped for the live best-effort form, or CapturedItemNameEscaped for the oracle).
		// `priceDisplay` is the bare number that precedes the currency token (e.g. "25").
		public static string BuildItemBought(string buyer, ulong itemInstanceId, string itemNameEscaped, string seller, string priceDisplay)
		{
			var sb = new StringBuilder();
			sb.Append(OuterOpen);
			Parameter(sb, "7", buyer ?? "");
			Parameter(sb, "1", itemInstanceId.ToString());
			Parameter(sb, "7", itemNameEscaped ?? "");
			Parameter(sb, "7", seller ?? "");
			Parameter(sb, "7", (priceDisplay ?? "0") + CurrencySuffix);
			sb.Append(OuterClose);
			return sb.ToString();
		}

		// A minimal item-name remoteformat for a live buy when only the item's RDB name string id is known (we lack
		// the per-item category/key the retail client emits). Renders the localized name by id; degrades to the id
		// itself if the client has no string for it. Escaped for embedding in a parameter value.
		public static string BuildItemNameEscaped(uint nameStringId)
		{
			var inner = $"<remoteformat id=\"{nameStringId}\" knubot=\"0\"></remoteformat>";
			return XmlEscape(inner);
		}

		private static void Parameter(StringBuilder sb, string type, string value)
		{
			sb.Append("<parameter value=\"").Append(type).Append(';').Append(value).Append("\" />");
		}

		// XML attribute/text escaping matching the client's parameter-value encoding (& < > " ').
		private static string XmlEscape(string s)
		{
			return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
		}
	}
}
