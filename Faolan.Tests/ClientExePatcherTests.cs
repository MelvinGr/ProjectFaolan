using System;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.ClientLauncher;
using Faolan.Core.Crypt;
using Xunit;

namespace Faolan.Tests
{
	// The launcher swaps the DH public key baked into AgeOfConan.exe for ours, so the server can decrypt the login
	// block and check the real password.
	//
	// It used to find that key by SEARCHING FOR RETAIL'S, which meant an exe could be patched exactly once: afterwards
	// it carries OUR key, so a second patch found nothing and reported NotFound. Rotating the server key
	// (`--generate-login-key`) therefore silently produced clients that could not log in, and the only fix - restore
	// from the .bak first - was written down nowhere.
	//
	// It now finds the slot by SHAPE: a NUL-delimited run of exactly 256 ASCII hex characters that is not the DH
	// prime. Both facts were read off the real exe - it holds precisely two such runs, the key at 0x01bf6c18 and the
	// prime 0x108 bytes later - and the exclusion is the part that carries risk, because the prime is the same width
	// in the same alphabet and overwriting it would break the handshake for every client.
	public class ClientExePatcherTests : IDisposable
	{
		private readonly string _exe = Path.Combine(Path.GetTempPath(), $"faolan-exe-{Guid.NewGuid():N}.bin");

		public void Dispose()
		{
			File.Delete(_exe);
		}

		// The retail case: the shipped key goes, ours arrives, the prime stays.
		[Fact]
		public void ItPatchesTheKeyAndLeavesThePrimeAlone()
		{
			var retail = Key('9');
			Write(retail);

			Assert.Equal(ClientExePatcher.Result.Patched, ClientExePatcher.PatchPublicKey(_exe, Key('a')));

			Assert.Equal(Key('a'), KeyInFile());
			Assert.Equal(DhKeys.PrimeHex, PrimeInFile()); // ...the run that must never be touched
			Assert.DoesNotContain(Encoding.ASCII.GetString(retail), File.ReadAllText(_exe));
		}

		// The bug this rewrite exists for: patch, then patch AGAIN with a different key - which is what rotating the
		// server's private key requires. The old value-search reported NotFound here.
		[Fact]
		public void ItRePatchesAnAlreadyPatchedExe()
		{
			Write(Key('9'));

			Assert.Equal(ClientExePatcher.Result.Patched, ClientExePatcher.PatchPublicKey(_exe, Key('a')));
			Assert.Equal(ClientExePatcher.Result.Patched, ClientExePatcher.PatchPublicKey(_exe, Key('b')));

			Assert.Equal(Key('b'), KeyInFile());
			Assert.Equal(DhKeys.PrimeHex, PrimeInFile());
		}

		// The same key twice is not a patch - the launcher reports it so, rather than rewriting the file for nothing.
		[Fact]
		public void ThePatchIsIdempotent()
		{
			Write(Key('9'));
			ClientExePatcher.PatchPublicKey(_exe, Key('a'));

			Assert.Equal(ClientExePatcher.Result.AlreadyDone, ClientExePatcher.PatchPublicKey(_exe, Key('a')));
		}

		// A file whose ONLY 256-hex run is the prime has no key slot. It must come back NotFound - the failure mode
		// that matters is not "did nothing", it is "patched the prime".
		[Fact]
		public void AFileWithOnlyThePrime_IsNotAKeySlot()
		{
			File.WriteAllBytes(_exe, [.. Nul(Encoding.ASCII.GetBytes(DhKeys.PrimeHex))]);

			Assert.Equal(ClientExePatcher.Result.NotFound, ClientExePatcher.PatchPublicKey(_exe, Key('a')));
			Assert.Equal(DhKeys.PrimeHex, File.ReadAllText(_exe).Trim('\0'));
		}

		// Hex is not enough on its own: the exe is full of hex-looking bytes, and only a NUL-delimited run of exactly
		// this width is a key field. A 255- or 257-character run is something else.
		[Theory]
		[InlineData(255)]
		[InlineData(257)]
		public void ARunOfTheWrongWidth_IsNotAKeySlot(int width)
		{
			File.WriteAllBytes(_exe, [.. Nul(Encoding.ASCII.GetBytes(new string('a', width)))]);

			Assert.Equal(ClientExePatcher.Result.NotFound, ClientExePatcher.PatchPublicKey(_exe, Key('a')));
		}

		// A synthetic exe with the real one's layout: the key field, some unrelated bytes, then the prime - each a
		// NUL-terminated string, as they sit in the client's string table.
		private void Write(byte[] key)
		{
			File.WriteAllBytes(_exe, [.. Nul(key), .. "some other string"u8.ToArray(), 0, .. Nul(Encoding.ASCII.GetBytes(DhKeys.PrimeHex))]);
		}

		private static byte[] Nul(byte[] value)
		{
			return [0, .. value, 0];
		}

		private static byte[] Key(char c)
		{
			return Encoding.ASCII.GetBytes(new string(c, DhKeys.PublicHexLength));
		}

		private byte[] KeyInFile()
		{
			return File.ReadAllBytes(_exe).Skip(1).Take(DhKeys.PublicHexLength).ToArray();
		}

		private string PrimeInFile()
		{
			var bytes = File.ReadAllBytes(_exe);
			return Encoding.ASCII.GetString(bytes, bytes.Length - DhKeys.PublicHexLength - 1, DhKeys.PublicHexLength);
		}
	}
}
