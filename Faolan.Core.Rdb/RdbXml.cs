using System;

namespace Faolan.Core.Rdb
{
	// Carves the XML element out of an RDB body, for the resource types whose payload is plain XML (MESHINDEXFILE
	// 1010030, texscript 1010035, monsterdata 1010201, combo defs 1010206, CHARACTERCREATOR 1010700).
	//
	// ⚠️ **Anchor on the ROOT TAG, never on the first `<`.** An RDB body is `[12-byte RDB-Id header][XML]` and that
	// header is BINARY - three u32s (type, id, marker) - so it contains the byte 0x3C ('<') whenever an id happens to
	// have one, which is ~6 % of entries. `text.IndexOf('<')` then anchors *inside the header* and hands
	// `XElement.Parse` a prefix of binary garbage; the parse throws and the caller's `catch` returns an empty record.
	// That silently lost **1,693 of 29,854** mesh manifests - a data-dependent failure, so it looked like nothing was
	// wrong until the parsed count was put next to the available count.
	//
	// ⚠️ **Both element forms are real.** An element with no children is written SELF-CLOSING (`<combo … />`); a carve
	// that requires a `</root>` drops those (151 of the 744 combo defs, Gitea #313).
	public static class RdbXml
	{
		// The `<rootTag>…</rootTag>` (or self-closing `<rootTag … />`) span of `text`, or null if there is none.
		// Trailing padding after the element is excluded, so the result parses on its own.
		public static string? Carve(string text, string rootTag)
		{
			var start = text.IndexOf("<" + rootTag, StringComparison.Ordinal);
			if (start < 0)
				return null;

			var close = text.IndexOf("</" + rootTag + ">", start, StringComparison.Ordinal);
			if (close >= 0)
				return text[start..(close + rootTag.Length + 3)];

			// No closing tag: the root itself is self-closing, so the first `/>` at or after it ends the element (a
			// child's `/>` can't come first - there are no children).
			var self = text.IndexOf("/>", start, StringComparison.Ordinal);
			return self < 0
				? null
				: text[start..(self + 2)];
		}
	}
}
