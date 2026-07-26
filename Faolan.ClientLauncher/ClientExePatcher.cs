using System;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.Core.Crypt;

namespace Faolan.ClientLauncher
{
	// Statically patch the AgeOfConan.exe FILE: swap the embedded Funcom DH pubkey for CELLAO_PUB (so the server can
	// validate the real password, docs/protocol/01-crypto.md) and optionally redirect the launcher-notes URL. Plain
	// byte edits - no ptrace, no process memory, so it works IDENTICALLY on every platform (the old /proc/<pid>/mem
	// in-memory patch was Linux-only). It persists on disk, so it runs BEFORE the exe launches and before the
	// -clienthash md5 (so the hash matches the file we run); the launcher backs the exe up to .bak first, so --restore
	// is a copy-back. Retail's ConanPatcher would re-verify/revert the exe, but we bypass it, so nothing clobbers it.
	internal static class ClientExePatcher
	{
		// Both 256-char ASCII hex DH public keys (g^priv mod N over the 1024-bit prime).
		//
		// DERIVED from the shared keypair in Faolan.Core (DhKeys), not a literal. This used to be a hand-copied hex
		// string while the private key lived in Faolan.Core.Network - two copies of one keypair, which fails silently:
		// change the private key and every login falls back to accept-any while the patched client still carries the
		// old public key. Deriving it means a configured Login:PrivateKey automatically patches the matching public key.
		public static byte[] NewPublicKey => Encoding.ASCII.GetBytes(DhKeys.ServerPublicHex);

		// The client's hardcoded launcher-notes URL - a printf FORMAT string with two %s (UniverseName + Language),
		// null-terminated in a fixed 73-byte region (offset 0x1C4D4C0). See memory client_launcher_notes_url.
		private static readonly byte[] OriginalNotesUrl = "http://msgs.ageofconan.com/launchernotes2.php?UniverseName=%s&Language=%s"u8.ToArray();

		// The key the RETAIL exe ships with - what we search for and replace. Also in DhKeys, so the launcher and any
		// other consumer agree on what "unpatched" looks like.
		private static readonly byte[] OriginalPublicKey = Encoding.ASCII.GetBytes(DhKeys.FuncomPublicHex);

		public enum Result
		{
			Patched, // the exe carried the Funcom (or Cellao, for Restore) key and was rewritten
			AlreadyDone, // the target key is already in place - nothing to do
			NotFound // neither key found - wrong/incompatible exe
		}

		// Redirect the launcher-notes URL to `newUrl` (retail's msgs.ageofconan.com host is dead, so the login screen
		// otherwise stalls fetching it). Overwrites the same fixed null-terminated region, so `newUrl` + its null must
		// fit the original 73 bytes; the tail is zero-filled. KEEP the two %s (UniverseName, Language) or the client's
		// sprintf mis-formats. Covered by the exe .bak on --restore (it lives in the exe).
		public static Result PatchLauncherNotes(string exePath, string newUrl)
		{
			var repl = Encoding.ASCII.GetBytes(newUrl);
			if (repl.Length >= OriginalNotesUrl.Length)
				return Result.NotFound; // too long for the fixed buffer (needs room for the null terminator)

			var bytes = File.ReadAllBytes(exePath);
			var at = IndexOf(bytes, OriginalNotesUrl, 0);
			if (at < 0)
			{
				return IndexOf(bytes, repl, 0) >= 0
					? Result.AlreadyDone
					: Result.NotFound;
			}

			for (var k = 0; k < OriginalNotesUrl.Length; k++)
			{
				bytes[at + k] = k < repl.Length
					? repl[k]
					: (byte)0;
			}

			File.WriteAllBytes(exePath, bytes);
			return Result.Patched;
		}

		public static Result PatchPublicKey(string exePath, byte[] newBytes)
		{
			var bytes = File.ReadAllBytes(exePath);

			var replaced = 0;
			for (var i = IndexOf(bytes, OriginalPublicKey, 0); i >= 0; i = IndexOf(bytes, OriginalPublicKey, i + OriginalPublicKey.Length))
			{
				newBytes.CopyTo(bytes, i);
				replaced++;
			}

			if (replaced > 0)
			{
				File.WriteAllBytes(exePath, bytes);
				return Result.Patched;
			}

			return IndexOf(bytes, newBytes, 0) >= 0
				? Result.AlreadyDone
				: Result.NotFound;
		}

		// A launcher-notes replacement is valid when it matches the client's fixed format contract: http:// only (the
		// exe statically links libcurl 7.19.7 with NO SSL backend, so https fails at runtime with cURL error 1
		// CURLE_UNSUPPORTED_PROTOCOL - front an https source with a plain-http proxy instead), EXACTLY two %s (the
		// UniverseName + Language the client sprintf-substitutes, in that order) and no other % conversion (a stray %,
		// or a third %s, makes the client read past the two args -> crash/garbage), and it must fit the fixed on-disk
		// buffer (ASCII, < the original region length, leaving room for the null terminator).
		public static bool ValidateNotesUrl(string url, out string error)
		{
			if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
			{
				error = "must be an http:// URL - the client's built-in libcurl 7.19.7 has no SSL, so https (or any " +
				        "other scheme) fails in-client with cURL error 1; proxy https content through a plain-http endpoint instead";
				return false;
			}

			var percents = url.Count(c => c == '%');
			var placeholders = url.Split("%s").Length - 1;
			if (placeholders != 2 || percents != 2)
			{
				error = $"must contain exactly two %s (UniverseName, Language) and no other '%' - found {placeholders} %s / {percents} '%'";
				return false;
			}

			var bytes = Encoding.ASCII.GetByteCount(url);
			if (bytes >= OriginalNotesUrl.Length)
			{
				error = $"too long ({bytes} bytes); must be < {OriginalNotesUrl.Length} to fit the client's fixed buffer (+ null terminator)";
				return false;
			}

			error = "";
			return true;
		}

		private static int IndexOf(byte[] haystack, byte[] needle, int start)
		{
			var last = haystack.Length - needle.Length;
			for (var i = start; i <= last; i++)
			{
				var j = 0;
				while (j < needle.Length && haystack[i + j] == needle[j])
					j++;
				if (j == needle.Length)
					return i;
			}

			return -1;
		}
	}
}
