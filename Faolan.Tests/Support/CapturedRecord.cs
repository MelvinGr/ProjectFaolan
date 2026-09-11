using System;

namespace Faolan.Tests.Support
{
	// A captured IIR body as a dump spells it: the carrier's [u32 len] and then the record itself. That length is the
	// sender's - Build returns the body without it - so a codec test has to drop it before handing the bytes to
	// Parse, and drop it again before comparing a Build against the capture.
	public static class CapturedRecord
	{
		// The record's own bytes.
		public static byte[] Bytes(string capturedHex)
		{
			return Convert.FromHexString(Hex(capturedHex));
		}

		// The same, still as hex - the form a golden compares against Convert.ToHexStringLower(rebuilt).
		public static string Hex(string capturedHex)
		{
			return capturedHex[8..]; // 4 bytes of u32 length = 8 hex chars
		}
	}
}
