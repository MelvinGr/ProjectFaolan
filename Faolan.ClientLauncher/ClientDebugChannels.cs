using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Faolan.ClientLauncher
{
	/// <summary>
	///     Turns on the retail client's OWN debug channels (`GameCode/Stats`, `GameCode/ArmorMitigation`,
	///     `GameCode/ItemData`, `N3.IIR`, …) by configuring its logger in the live process, so the client tells us what
	///     it computed instead of us inferring it from screenshots.
	/// </summary>
	/// <remarks>
	///     Verified live 2026-07-26 (see docs/tooling/client-debug-logging.md). The logger's config object sits at
	///     <c>0x02984AC0</c> and the client is PE32 with no ASLR, so Ghidra's addresses ARE the runtime ones:
	///     <list type="bullet">
	///         <item><c>0x02984AC0</c> — the DEFAULT level threshold. Ships as <b>3</b>.</item>
	///         <item><c>0x02984ADC</c> — output mode. Ships as <b>2</b> = per-channel thresholds.</item>
	///         <item><c>0x02984AE0</c> — 0x80 channel names, 48 bytes each, NUL-padded.</item>
	///         <item><c>0x029862E0</c> — the parallel i32 threshold per name.</item>
	///     </list>
	///     A line is emitted when <c>threshold &lt;= level</c>. The debug sites log at level <b>0–2</b> while ERROR is 4,
	///     so the shipped default of 3 is exactly what silences them — and it ships with only
	///     <c>CallTrace</c>/<c>AssertMode</c>/<c>PrintModule</c> in the table. Adding a channel with threshold 0 opens
	///     that channel and nothing else.
	///     <para>
	///         ⚠️ Do NOT just drop the default to 0: measured, that produces ~117k lines in seconds
	///         (<c>GameCode.Movement</c> alone logs per entity per frame). Per-channel is the usable form.
	///     </para>
	///     <para>
	///         ⚠️ The mode-2 lookup walks the name table and STOPS at the first empty entry, so names must stay
	///         CONTIGUOUS - a gap hides everything after it.
	///     </para>
	///     Output goes to <c>AgeOfConan.log</c> (the logger is initialised, and <c>0x029864E0</c> already forces a flush
	///     per line, so <c>--console</c> tails it live). A PE-subsystem GUI→CUI flip is NOT needed: that was tried and
	///     the channels stayed silent, because the stderr branch of the sink only runs when the logger is UNinitialised.
	/// </remarks>
	internal static class ClientDebugChannels
	{
		// Useful and quiet: the channels that answer open questions, minus the per-frame firehoses (Movement,
		// Scry.ScriptcommandsTrace, CommandQueue, Hiding). `strings AgeOfConan.exe | grep '^GameCode/'` lists all ~90.
		public static readonly string[] Default =
		[
			// EVERY stat the client applies, with its PREVIOUS value: "CMsg_StatChange()..(<id> <new> old:<old>)".
			// Strictly more than GameCode/Stats, which only shows server-driven sets - this also catches what the client
			// computes or derives itself, which is how the attribute pipeline was found (#442/#444): the five attributes
			// start at a hardcoded 10, then each loaded item template's contribution lands as its own write, so the log
			// attributes a delta to an item id. Pairs with GameCode/ItemData, which names the template being loaded.
			"ClientInterface",
			"GameCode/Stats", // every stat the server sends, by id and value - "got new stats: 507=291000"
			"GameCode/ArmorMitigation", // TotalArmorValue / Adjusted / Min+MaxMitigation / Final Mitigation per hit
			"GameCode/ItemData", // item template load + per-item static data
			"GameCode/Magic/Calculation", // spell magnitude computation
			"GameCode/Effects", // visual effect start/stop, by resource id
			"N3.IIR" // "RECEIVED IIR: class <Name>IIR_c" - the inbound IIR trace
		];

		private const long ConfigBase = 0x02984AC0; // [0] default threshold, [7] output mode
		private const long NameTable = 0x02984AE0; // 0x80 x 48-byte NUL-padded channel names
		private const long ThresholdTable = 0x029862E0; // 0x80 x i32, parallel to NameTable
		private const int NameStride = 48;
		private const int MaxEntries = 0x80;
		private const int ModeOffset = 0x1C; // ConfigBase + 7*4

		/// <summary>
		///     Waits for the client's PE process to appear, then installs <paramref name="channels" /> with threshold 0.
		///     Reports what it did through <paramref name="log" />; never throws - a failed probe leaves the client running
		///     with logging simply not enabled.
		/// </summary>
		public static async Task InstallAsync(IReadOnlyCollection<string> channels, Action<string> log, CancellationToken cancellationToken = default)
		{
			if (!OperatingSystem.IsLinux())
			{
				log("debug-log  : only implemented on Linux (needs /proc/<pid>/mem).");
				return;
			}

			// The client is launched through proton -> steam.exe -> AgeOfConan.exe, so the PE process shows up a few
			// seconds after Process.Start.
			var pid = await WaitForClientAsync(TimeSpan.FromSeconds(90), cancellationToken);
			if (pid == null)
			{
				log("debug-log  : the client's PE process never appeared; channels not enabled.");
				return;
			}

			try
			{
				// The logger's output mode is not its final value when the PE process first appears: measured, it reads
				// 0 early in startup and only becomes 2 (per-channel thresholds) once the client applies its logging
				// config. There are three setters for it in the client (FUN_004b2c30/60/90 -> mode 0/1/2), so waiting
				// for the steady state is what makes the install stick instead of being configured over.
				if (!await WaitForModeAsync(pid.Value, 2, TimeSpan.FromMinutes(3), cancellationToken))
				{
					log("debug-log  : the client logger never reached output mode 2 (per-channel thresholds); channels not enabled.");
					return;
				}

				Install(pid.Value, channels, log);
			}
			catch (UnauthorizedAccessException)
			{
				log($"debug-log  : cannot write /proc/{pid}/mem. This launcher is an ANCESTOR of the client so " +
				    "ptrace_scope=1 should allow it; if it does not, set kernel.yama.ptrace_scope=0.");
			}
			catch (Exception ex)
			{
				log($"debug-log  : failed to configure the client logger: {ex.Message}");
			}
		}

		// Polls the client's logger output mode until it reaches `mode`. Cheap (one 4-byte read per poll) and the only
		// way to know the config has settled - the client offers no signal for it.
		private static async Task<bool> WaitForModeAsync(int pid, int mode, TimeSpan timeout, CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + timeout;
			while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
			{
				try
				{
					using var mem = new FileStream($"/proc/{pid}/mem", FileMode.Open, FileAccess.Read);
					if (ReadInt32(mem, ConfigBase + ModeOffset) == mode)
						return true;
				}
				catch (IOException)
				{
					return false; // the process went away
				}

				await Task.Delay(1000, cancellationToken);
			}

			return false;
		}

		private static void Install(int pid, IReadOnlyCollection<string> channels, Action<string> log)
		{
			using var mem = new FileStream($"/proc/{pid}/mem", FileMode.Open, FileAccess.ReadWrite);

			var mode = ReadInt32(mem, ConfigBase + ModeOffset);
			var defaultThreshold = ReadInt32(mem, ConfigBase);
			if (mode != 2)
			{
				// Mode 0 is a whitelist by NAME (no thresholds) and mode 1 a blacklist; the per-channel threshold path
				// this writes only exists in mode 2. Should not happen - the caller waited for mode 2 - so report it
				// rather than writing something that has no effect.
				log($"debug-log  : client logger is in output mode {mode}, expected 2 (per-channel thresholds); not touching it.");
				return;
			}

			// Existing names must stay contiguous, so append after the last used slot.
			var used = 0;
			while (used < MaxEntries && ReadByte(mem, NameTable + used * NameStride) != 0)
				used++;

			var installed = new List<string>();
			foreach (var channel in channels.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct())
			{
				var bytes = Encoding.ASCII.GetBytes(channel);
				if (bytes.Length >= NameStride)
				{
					log($"debug-log  : channel '{channel}' is too long for the client's {NameStride}-byte slot; skipped.");
					continue;
				}

				if (used >= MaxEntries)
				{
					log($"debug-log  : the client's channel table is full ({MaxEntries}); '{channel}' skipped.");
					break;
				}

				var slot = new byte[NameStride];
				bytes.CopyTo(slot, 0);
				Write(mem, NameTable + used * NameStride, slot);
				Write(mem, ThresholdTable + used * 4, BitConverter.GetBytes(0));
				used++;
				installed.Add(channel);
			}

			if (installed.Count == 0)
			{
				log("debug-log  : no channels installed.");
				return;
			}

			// Read back so a silent failure (a write that lands nowhere) cannot be mistaken for success.
			var verified = installed.Where((c, i) => ReadName(mem, used - installed.Count + i) == c).ToList();
			log($"debug-log  : {verified.Count}/{installed.Count} channel(s) enabled in the live client (default threshold {defaultThreshold} " +
			    $"left alone, so nothing else got louder): {string.Join(", ", verified)}");
			log("debug-log  : output goes to AgeOfConan.log - it is flushed per line, so --console shows it live.");
		}

		private static string ReadName(FileStream mem, int index)
		{
			var buf = new byte[NameStride];
			mem.Seek(NameTable + index * NameStride, SeekOrigin.Begin);
			mem.ReadExactly(buf);
			var end = Array.IndexOf(buf, (byte)0);
			return Encoding.ASCII.GetString(buf, 0, end < 0
				? buf.Length
				: end);
		}

		private static byte ReadByte(FileStream mem, long address)
		{
			var buf = new byte[1];
			mem.Seek(address, SeekOrigin.Begin);
			mem.ReadExactly(buf);
			return buf[0];
		}

		private static int ReadInt32(FileStream mem, long address)
		{
			var buf = new byte[4];
			mem.Seek(address, SeekOrigin.Begin);
			mem.ReadExactly(buf);
			return BinaryPrimitives.ReadInt32LittleEndian(buf);
		}

		private static void Write(FileStream mem, long address, byte[] bytes)
		{
			mem.Seek(address, SeekOrigin.Begin);
			mem.Write(bytes);
			mem.Flush();
		}

		// The AgeOfConan.exe PE process is the one whose maps carry the image at its 0x400000 base - proton and the
		// wine steam.exe shim both match on cmdline, and only this one has the PE mapped.
		private static async Task<int?> WaitForClientAsync(TimeSpan timeout, CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + timeout;
			while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
			{
				foreach (var dir in Directory.EnumerateDirectories("/proc"))
				{
					if (!int.TryParse(Path.GetFileName(dir), out var pid))
						continue;

					try
					{
						foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
						{
							if (line.StartsWith("00400000-", StringComparison.Ordinal) && line.Contains("AgeOfConan", StringComparison.OrdinalIgnoreCase))
								return pid;
						}
					}
					catch
					{
						// the process exited, or is not ours - just skip it
					}
				}

				await Task.Delay(500, cancellationToken);
			}

			return null;
		}
	}
}
