using System.Globalization;
using System.Text;

namespace Faolan.ClientLauncher
{
	// In-launcher port of tools/patch_aoc_login_key.py: overwrite the client's embedded Funcom DH pubkey with
	// CELLAO_PUB in memory so the server can validate the real password (see docs/login-decryption-plan.md). The
	// client maps its PE MAP_PRIVATE, so a /proc/<pid>/mem write copies-on-write and changes only the live process;
	// the game file on disk is untouched. Needs ptrace permission for the target - works as root, and often as the
	// launching ancestor under the default yama ptrace_scope.
	internal static class LoginKeyPatcher
	{
		// Both 256-char ASCII hex DH public keys (g^priv mod N over the 1024-bit prime).
		private static readonly byte[] Funcom = Encoding.ASCII.GetBytes(
			"9c32cc23d559ca90fc31be72df817d0e124769e809f936bc14360ff4bed758f2" +
			"60a0d596584eacbbc2b88bdd410416163e11dbf62173393fbc0c6fefb2d855f1" +
			"a03dec8e9f105bbad91b3437d8eb73fe2f44159597aa4053cf788d2f9d7012fb8" +
			"d7c4ce3876f7d6cd5d0c31754f4cd96166708641958de54a6def5657b9f2e92");

		private static readonly byte[] Cellao = Encoding.ASCII.GetBytes(
			"26b5a3b4ac1177f24a2d9de44bafef477ff23ef1cb5f646919b1be2651605303" +
			"0b65d5afb60cef6f49de539958ba0b7922a099319b8016a8673cb27a696ae4b6" +
			"0fdece25ddcdad42e7f0056b87fc35687fe033b242e17e960d79806fd46c4a79" +
			"cbc64f558660a50cabc1c242dace70de6af452e3433f97e30e202567f187de70");

		private enum Result { NotReady, Patched, NoPermission }

		// Poll for the client process + its DH key and patch it, before it logs in. The key is mapped at startup,
		// so polling beats the (network) login handshake.
		public static void PatchWhenReady(int timeoutMs = 30000)
		{
			var deadline = Environment.TickCount64 + timeoutMs;
			var sawProcess = false;

			while (Environment.TickCount64 < deadline)
			{
				if (FindClientPid() is int pid)
				{
					sawProcess = true;
					switch (TryPatch(pid))
					{
						case Result.Patched:
							Console.WriteLine($"login-key: patched DH pubkey in pid {pid} (password validation enabled).");
							return;
						case Result.NoPermission:
							Console.Error.WriteLine("login-key: need ptrace permission to patch (run the launcher with sudo); " +
								"launching without password validation.");
							return;
					}
				}

				Thread.Sleep(150);
			}

			Console.Error.WriteLine(sawProcess
				? "login-key: DH key not located before timeout; skipped patch."
				: "login-key: client process not found; skipped patch.");
		}

		private static int? FindClientPid()
		{
			foreach (var dir in Directory.EnumerateDirectories("/proc"))
			{
				if (!int.TryParse(Path.GetFileName(dir), out var pid))
					continue;
				try
				{
					if (File.ReadAllText(Path.Combine(dir, "comm")).Trim().StartsWith("AgeOfConan", StringComparison.Ordinal))
						return pid;
				}
				catch
				{
					// vanished/unreadable
				}
			}

			return null;
		}

		private static Result TryPatch(int pid)
		{
			Microsoft.Win32.SafeHandles.SafeFileHandle handle;
			try
			{
				handle = File.OpenHandle($"/proc/{pid}/mem", FileMode.Open, FileAccess.ReadWrite);
			}
			catch (UnauthorizedAccessException)
			{
				return Result.NoPermission;
			}
			catch
			{
				return Result.NotReady;
			}

			using (handle)
			{
				string[] maps;
				try
				{
					maps = File.ReadAllLines($"/proc/{pid}/maps");
				}
				catch
				{
					return Result.NotReady;
				}

				const int chunk = 8 << 20;
				var overlap = Funcom.Length - 1;

				foreach (var line in maps)
				{
					var dash = line.IndexOf('-');
					var space = line.IndexOf(' ');
					if (dash < 0 || space < 0 || space - dash - 1 <= 0 || line.Length < space + 5)
						continue;
					if (line[space + 1] != 'r') // need read to scan
						continue;
					if (!ulong.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, null, out var start) ||
					    !ulong.TryParse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber, null, out var end))
						continue;
					if (line.EndsWith("[vvar]", StringComparison.Ordinal) || line.EndsWith("[vsyscall]", StringComparison.Ordinal))
						continue;

					var pos = start;
					byte[] prevTail = [];
					while (pos < end)
					{
						var want = (int)Math.Min((ulong)chunk, end - pos);
						var data = new byte[want];
						int read;
						try
						{
							read = RandomAccess.Read(handle, data, (long)pos);
						}
						catch
						{
							break; // unreadable region (special mapping)
						}

						if (read <= 0)
							break;

						var hay = new byte[prevTail.Length + read];
						prevTail.CopyTo(hay, 0);
						Array.Copy(data, 0, hay, prevTail.Length, read);
						var hayBase = pos - (ulong)prevTail.Length;

						for (var idx = IndexOf(hay, Funcom, 0); idx >= 0; idx = IndexOf(hay, Funcom, idx + 1))
						{
							var hitAddr = (long)(hayBase + (ulong)idx);
							try
							{
								RandomAccess.Write(handle, Cellao, hitAddr);
								var verify = new byte[Cellao.Length];
								RandomAccess.Read(handle, verify, hitAddr);
								if (verify.AsSpan().SequenceEqual(Cellao))
									return Result.Patched;
							}
							catch (UnauthorizedAccessException)
							{
								return Result.NoPermission;
							}
							catch
							{
								// page not writable right now; keep scanning
							}
						}

						prevTail = read >= overlap ? data[(read - overlap)..] : data[..read];
						pos += (ulong)read;
					}
				}
			}

			return Result.NotReady;
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
