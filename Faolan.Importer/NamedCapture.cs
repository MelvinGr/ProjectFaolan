using System.Collections.Generic;
using Faolan.Core.Network;

namespace Faolan.Importer
{
	// A capture's packets plus its base filename (no extension). The name lets per-capture rules (e.g.
	// quest-state dialogue conditions) target a specific capture. Top-level (not nested in Program) so the
	// v2 importer surface can expose it on public types.
	public sealed record NamedCapture(string Name, List<CapturedPacket> Packets);
}
