using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchOpenSiteInBrowserOnClient(siteName) - GameCharInterfaceProject 0x0F. Makes the client open a URL in the
	// player's external browser (#874).
	//
	//   [u16 len][ascii siteName]
	//
	// All three captured frames are byte-identical, 54 bytes, and carry Funcom's item-shop deep link
	// (`http://aocshop.funcom.com/showitem?bundle=quickstart`) - a store promotion, not a game mechanic.
	//
	// ⚠️ **Think before sending one.** This message makes a player's machine launch a browser at a server-chosen
	// address. Retail used it for its own shop; anything we send here is the same capability pointed somewhere else,
	// so it should carry an address the operator configured, never one derived from player input.
	public static class OpenSiteInBrowserCodec
	{
		public const uint Method = (uint)GameCharInterfaceProjectMethods.DispatchOpenSiteInBrowserOnClient;

		public static byte[] Build(OpenSiteInBrowser value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(string siteName)
		{
			return Build(new OpenSiteInBrowser(siteName));
		}

		public static OpenSiteInBrowser Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(OpenSiteInBrowserCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static OpenSiteInBrowser Read(ref SpanReader r)
		{
			return new OpenSiteInBrowser(r.ReadStringU16Length());
		}

		internal static void Write(FaolanStream s, OpenSiteInBrowser v)
		{
			s.WriteString(v.SiteName);
		}
	}

	public sealed record OpenSiteInBrowser(string SiteName);
}
