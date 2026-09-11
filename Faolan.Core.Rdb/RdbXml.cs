using System;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     Carves the XML element out of an RDB body, for the resource types whose payload is plain XML
	///     (MESHINDEXFILE 1010030, texscript 1010035, monsterdata 1010201, combo defs 1010206, CHARACTERCREATOR
	///     1010700).
	/// </summary>
	/// <remarks>
	///     Anchor on the root tag, never on the first <c>&lt;</c>. An RDB body is
	///     <c>[12-byte RDB-Id header][XML]</c> and that header is binary - three u32s (type, id, marker) - so it
	///     contains the byte 0x3C ('&lt;') whenever an id happens to have one, which is ~6% of entries.
	///     <c>text.IndexOf('&lt;')</c> then anchors inside the header and hands <c>XElement.Parse</c> a prefix of
	///     binary garbage; the parse throws and a caller's <c>catch</c> returns an empty record. That silently loses
	///     1,693 of 29,854 mesh manifests - a data-dependent failure, invisible until the parsed count is put next to
	///     the available count.
	///     <para />
	///     Both element forms are real. An element with no children is written self-closing (<c>&lt;combo ... /&gt;</c>);
	///     a carve that requires a <c>&lt;/root&gt;</c> drops those (151 of the 744 combo defs).
	/// </remarks>
	public static class RdbXml
	{
		/// <summary>
		///     The <c>&lt;rootTag&gt;...&lt;/rootTag&gt;</c> (or self-closing <c>&lt;rootTag ... /&gt;</c>) span of
		///     <paramref name="text" />, or null if there is none. Trailing padding after the element is excluded, so
		///     the result parses on its own.
		/// </summary>
		public static string? Carve(string text, string rootTag)
		{
			var start = text.IndexOf("<" + rootTag, StringComparison.Ordinal);
			if (start < 0)
				return null;

			var close = text.IndexOf("</" + rootTag + ">", start, StringComparison.Ordinal);
			if (close >= 0)
				return text[start..(close + rootTag.Length + 3)];

			// No closing tag: the root itself is self-closing, so the first `/>` at or after it ends the element (a
			// child's `/>` cannot come first - there are no children).
			var self = text.IndexOf("/>", start, StringComparison.Ordinal);
			return self < 0
				? null
				: text[start..(self + 2)];
		}
	}
}
