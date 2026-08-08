using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.Core.Crypt;

namespace Faolan.ClientLauncher
{
	// Statically patch the AgeOfConan.exe FILE: swap the embedded Funcom DH pubkey for the one the caller hands in (so
	// the server can validate the real password, docs/protocol/01-crypto.md) and optionally redirect the launcher-notes
	// URL. The key is a PARAMETER of PatchPublicKey rather than something this class resolves from configuration: which
	// key belongs in a client is the launcher's decision (derived from Server:PrivateKey, or an explicit
	// Server:PublicKey for somebody else's server), and it is stated at the call site. Plain
	// byte edits - no ptrace, no process memory, so it works IDENTICALLY on every platform (the old /proc/<pid>/mem
	// in-memory patch was Linux-only). It persists on disk, so it runs BEFORE the exe launches and before the
	// -clienthash md5 (so the hash matches the file we run); the launcher backs the exe up to .bak first, so --restore
	// is a copy-back. Retail's ConanPatcher would re-verify/revert the exe, but we bypass it, so nothing clobbers it.
	internal static class ClientExePatcher
	{
		// The client's hardcoded launcher-notes URL - a printf FORMAT string with two %s (UniverseName + Language),
		// null-terminated in a fixed 73-byte region (offset 0x1C4D4C0). See memory client_launcher_notes_url.
		private static readonly byte[] OriginalNotesUrl = "http://msgs.ageofconan.com/launchernotes2.php?UniverseName=%s&Language=%s"u8.ToArray();

		// The DH prime, as the exe stores it. NOT a patch target - it is the one OTHER 256-char hex string in the
		// file, and the key slot is found by excluding it.
		private static byte[] Prime => Encoding.ASCII.GetBytes(ClientLoginEncryption.PrimeHex);

		public enum Result
		{
			Patched, // the key slot held some other key and was rewritten
			AlreadyDone, // the target key is already in place - nothing to do
			NotFound // no key slot in this file - wrong/incompatible exe
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

		// Writes `newBytes` into every DH public-key slot in the exe.
		//
		// ⚠️ The slot is found by SHAPE, not by the value retail ships. Searching for retail's key meant an exe could be
		// patched exactly once: after that it carries OUR key, so a second patch - which is what rotating the server
		// key needs (`--generate-login-key`) - found nothing and reported NotFound. You had to --restore from the .bak
		// first, and nothing said so.
		//
		// The shape is exact and was read off the file: a NUL-delimited run of exactly 256 ASCII hex characters. There
		// are precisely two such runs in AgeOfConan.exe (and in AgeOfConanDX10.exe) - the public key at 0x01bf6c18 and
		// the DH prime 0x108 bytes after it - so "the 256-hex string that is not the prime" names the key slot whatever
		// value is currently in it. Excluding the prime is the part that matters: it is the same width and the same
		// alphabet, and overwriting it would break the handshake for every client, patched or not.
		public static Result PatchPublicKey(string exePath, byte[] newBytes)
		{
			var bytes = File.ReadAllBytes(exePath);
			var slots = PublicKeySlots(bytes);
			if (slots.Count == 0)
				return Result.NotFound;

			if (slots.All(at => bytes.AsSpan(at, newBytes.Length).SequenceEqual(newBytes)))
				return Result.AlreadyDone;

			foreach (var at in slots)
				newBytes.CopyTo(bytes, at);

			File.WriteAllBytes(exePath, bytes);

			return Result.Patched;
		}

		// Every offset holding a DH public key: a maximal run of exactly PublicHexLength ASCII hex characters, NUL on
		// both sides, that is not the prime.
		private static List<int> PublicKeySlots(byte[] bytes)
		{
			var slots = new List<int>();
			var prime = Prime;
			for (var i = 0; i < bytes.Length; i++)
			{
				if (!IsHex(bytes[i]))
					continue;

				var start = i;
				while (i < bytes.Length && IsHex(bytes[i]))
					i++;

				if (i - start != ClientLoginEncryption.PublicHexLength || start == 0 || bytes[start - 1] != 0 || i >= bytes.Length || bytes[i] != 0)
					continue;

				if (!bytes.AsSpan(start, prime.Length).SequenceEqual(prime))
					slots.Add(start);
			}

			return slots;
		}

		private static bool IsHex(byte b)
		{
			return b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
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
