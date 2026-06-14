using System;

namespace Faolan.Core.Crypt
{
	// The ConanPatcher "-key" launch token. Recovered by RE of ConanPatcher.exe (2026-06-09): the patcher reads
	// t = time(NULL) (sub_4bcf93: GetSystemTimeAsFileTime -> minus the 1601 epoch -> /10^7 = unix seconds), sets a
	// 64-bit block to {t, t}, TEA-encrypts it (sub_439140: 32 rounds, delta 0x9e3779b9, the hardcoded key below),
	// and formats the two result words as %08x%08x. So `-key` is a launch-FRESHNESS token: decrypting it yields
	// {t, t} (equal halves) and the timestamp must be recent. AgeOfConan.exe bounces to ConanPatcher.exe when
	// launched without a valid fresh -key (the no-flags case). Validated: the captured `495cd3b8c7047b47` decrypts
	// to t = 1781038901 (= the launch time), and re-encrypting reproduces it exactly.
	public static class ConanLaunchToken
	{
		// k0..k3, the TEA key baked into the patcher (the magic constants in sub_439140).
		private static readonly uint[] PatcherKey = [0x5BCE568E, 0xFB2E8CBC, 0xA324E6D8, 0xF22BF865];

		/// <summary>The <c>-key</c> value for a given Unix timestamp (seconds).</summary>
		public static string ForUnixTime(uint unixSeconds)
		{
			uint v0 = unixSeconds, v1 = unixSeconds;
			Tea.EncryptBlock(ref v0, ref v1, PatcherKey);
			return $"{v0:x8}{v1:x8}";
		}

		/// <summary>
		///     Inverse: recover the timestamp from a token, or null if it's malformed or the decrypted halves don't
		///     match (i.e. not a genuine token). This is what a strict server would check, plus a freshness window.
		/// </summary>
		public static uint? GetUnixTime(string token)
		{
			if (token is not { Length: 16 })
				return null;

			try
			{
				var v0 = Convert.ToUInt32(token[..8], 16);
				var v1 = Convert.ToUInt32(token[8..], 16);
				Tea.DecryptBlock(ref v0, ref v1, PatcherKey);
				return v0 == v1
					? v0
					: null;
			}
			catch (FormatException)
			{
				return null;
			}
		}

		/// <summary>The <c>-key</c> value for the current time.</summary>
		public static string Now()
		{
			return ForUnixTime((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		}
	}
}
