using System.Collections.Generic;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Captures
{
	// A capture's packets plus its base filename (no extension). The name lets per-capture rules (e.g.
	// quest-state dialogue conditions) target a specific capture. Top-level (not nested in Program) so the
	// importer module surface can expose it on public types.
	public sealed record NamedCapture(string Name, List<CapturedPacket> Packets);
}
