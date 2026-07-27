using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Captures
{
	// A capture's packets plus its base filename (no extension). The name lets per-capture rules (e.g.
	// quest-state dialogue conditions) target a specific capture. Top-level (not nested in Program) so the
	// importer module surface can expose it on public types.
	public sealed record NamedCapture(string Name, List<CapturedPacket> Packets)
	{
		private const string DumpSuffix = ".tshark.json";

		// Every capture in a directory, in enumeration order. This is what `--captures <dir>` means, and it lived as
		// a byte-identical private copy in BOTH CLIs (ImportCli and ReportCli) - the loader for a type belongs on the
		// type, the same way CapturedPacket.Body does.
		//
		// Only *.tshark.json is read: that is the canonical TCP-reassembled dump tools/capture/tshark_dump.sh
		// produces, and the one form that carries the compressed GameServer downloads.
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
