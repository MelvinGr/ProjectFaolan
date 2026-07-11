using System.IO;
using System.Linq;
using System.Text;

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
		public static readonly byte[] CellAo =
			"26b5a3b4ac1177f24a2d9de44bafef477ff23ef1cb5f646919b1be26516053030b65d5afb60cef6f49de539958ba0b7922a099319b8016a8673cb27a696ae4b60fdece25ddcdad42e7f0056b87fc35687fe033b242e17e960d79806fd46c4a79cbc64f558660a50cabc1c242dace70de6af452e3433f97e30e202567f187de70"u8
				.ToArray();

		private static readonly byte[] Funcom =
			"9c32cc23d559ca90fc31be72df817d0e124769e809f936bc14360ff4bed758f260a0d596584eacbbc2b88bdd410416163e11dbf62173393fbc0c6fefb2d855f1a03dec8e9f105bbad91b3437d8eb73fe2f44159597aa4053cf788d2f9d7012fb8d7c4ce3876f7d6cd5d0c31754f4cd96166708641958de54a6def5657b9f2e92"u8
				.ToArray();

		// The client's hardcoded launcher-notes URL - a printf FORMAT string with two %s (UniverseName + Language),
		// null-terminated in a fixed 73-byte region (offset 0x1C4D4C0). See memory client_launcher_notes_url.
		private static readonly byte[] NotesUrlOld = "http://msgs.ageofconan.com/launchernotes2.php?UniverseName=%s&Language=%s"u8.ToArray();

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
			if (repl.Length >= NotesUrlOld.Length)
				return Result.NotFound; // too long for the fixed buffer (needs room for the null terminator)

			var bytes = File.ReadAllBytes(exePath);
			var at = IndexOf(bytes, NotesUrlOld, 0);
			if (at < 0)
			{
				return IndexOf(bytes, repl, 0) >= 0
					? Result.AlreadyDone
					: Result.NotFound;
			}

			for (var k = 0; k < NotesUrlOld.Length; k++)
			{
				bytes[at + k] = k < repl.Length
					? repl[k]
					: (byte)0;
			}

			File.WriteAllBytes(exePath, bytes);
			return Result.Patched;
		}

		public static Result PatchLoginKey(string exePath, byte[] newBytes)
		{
			var bytes = File.ReadAllBytes(exePath);

			var replaced = 0;
			for (var i = IndexOf(bytes, Funcom, 0); i >= 0; i = IndexOf(bytes, Funcom, i + Funcom.Length))
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

		// A launcher-notes replacement is valid when it matches the client's fixed format contract: EXACTLY two %s (the
		// UniverseName + Language the client sprintf-substitutes, in that order) and no other % conversion (a stray %,
		// or a third %s, makes the client read past the two args -> crash/garbage), and it must fit the fixed on-disk
		// buffer (ASCII, < the original region length, leaving room for the null terminator).
		public static bool ValidateNotesUrl(string url, out string error)
		{
			var percents = url.Count(c => c == '%');
			var placeholders = url.Split("%s").Length - 1;
			if (placeholders != 2 || percents != 2)
			{
				error = $"must contain exactly two %s (UniverseName, Language) and no other '%' - found {placeholders} %s / {percents} '%'";
				return false;
			}

			var bytes = Encoding.ASCII.GetByteCount(url);
			if (bytes >= NotesUrlOld.Length)
			{
				error = $"too long ({bytes} bytes); must be < {NotesUrlOld.Length} to fit the client's fixed buffer (+ null terminator)";
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
