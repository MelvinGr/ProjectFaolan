using System;
using System.Collections.Generic;
using System.IO;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     A capture's packets plus its base filename (no extension). The name lets per-capture rules (e.g.
	///     quest-state dialogue conditions) target a specific capture.
	/// </summary>
	public sealed record NamedCapture(string Name, List<CapturedPacket> Packets)
	{
		private const string DumpSuffix = ".tshark.json";

		/// <summary>
		///     Every capture in a directory, in enumeration order - what `--captures <dir>` means.
		/// </summary>
		/// <remarks>
		///     Only *.tshark.json is read: that is the canonical TCP-reassembled dump tools/capture/tshark_dump.sh
		///     produces, and the one form that carries the compressed GameServer downloads.
		/// </remarks>
		public static List<NamedCapture> LoadAll(string capturesDir)
		{
			var captures = new List<NamedCapture>();
			foreach (var dump in Directory.EnumerateFiles(capturesDir, $"*{DumpSuffix}"))
			{
				var name = Path.GetFileName(dump);
				if (name.EndsWith(DumpSuffix, StringComparison.OrdinalIgnoreCase))
					name = name[..^DumpSuffix.Length];

				captures.Add(new NamedCapture(name, TsharkCaptureReader.Read(dump)));
			}

			return captures;
		}
	}
}
