using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Importer.Extractors
{
	// where a zone-line door leads, for the doors no capture ever walked through.
	//
	// A DoorFullUpdateIIR zone-line is named for the playfield behind it - the client streams that name in the door's
	// own body ("Conall's Valley", "To Lacheish Plains", "Villa Amiel") - so the client's own playfield table (Maps,
	// RDB 1070003, 520 playfields) answers the question the corpus cannot. WorldTeleportExtractor stays the truth: it
	// harvests the destination whole off the wire for the ~20 doors a capture walked through. This fills the rest.
	//
	// Checked against that harvest, which is the only ground truth there is: of the captured cross-map doors whose
	// name matches a playfield, every one names the playfield the capture actually teleported to - Conall's Valley
	// (1010), Conarch Village (1000), Field of the Dead (1020), Spider Caverns (1004), Ymir's Pass (1090), Tarantia
	// Noble District (2010), Old Tarantia (2000), Thunder River (2090), City of Tortage (4020). Zero mismatches.
	//
	// It is name evidence, not wire evidence, so it deliberately stops short of the arrival position: a name-resolved
	// row carries a destination map with a null position, and the runtime lands the player on that map's default
	// spawn - exactly what `.teleport <map>` already does - or reports the gap when the map defines no spawn point.
	public static class ZoneLineNames
	{
		// A playfield a door name could mean. HasSpawnPoint is what separates the real playfield from the client's
		// content-free duplicate rows (see PickDestination) and decides whether the runtime can land anyone there.
		public readonly record struct MapCandidate(uint MapId, bool HasSpawnPoint);

		// Level-editor variants of one locale, not separate destinations: the Tortage day/night pair is "City of
		// Tortage" / "City of Tortage Night". Used only to recognise a door that leads back into its own playfield.
		private static readonly string[] VariantSuffixes = [" Night", " Day"];

		// The playfield name a door is asking for: its name minus the "To " the level designers prefix some with
		// ("To Lacheish Plains", "To Ymir's Pass"). Null when the door carries no usable name.
		public static string? Key(string? doorName)
		{
			var name = doorName?.Trim();
			if (string.IsNullOrEmpty(name))
				return null;

			if (name.StartsWith("To ", StringComparison.Ordinal))
				name = name[3..].Trim();

			return string.IsNullOrEmpty(name)
				? null
				: name;
		}

		// True when the door names the playfield it is standing in - the City of Tortage doors on 4020/4022 are the
		// case, and they are same-playfield relocations into a building, not zone-lines. Their day/night twin is a
		// different map id, so an id comparison misses them; the base name is what matches. Nothing here can supply
		// the arrival position such a door needs, so it is left inert rather than sent to the wrong Tortage.
		public static bool LeadsBackIntoOwnPlayfield(string key, string? sourceMapName)
		{
			var source = sourceMapName?.Trim();
			if (string.IsNullOrEmpty(source))
				return false;

			foreach (var suffix in VariantSuffixes)
			{
				if (source.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					source = source[..^suffix.Length].TrimEnd();
					break;
				}
			}

			return string.Equals(source, key, StringComparison.OrdinalIgnoreCase);
		}

		// Which of the same-named playfields the door means. The client ships DUPLICATE playfield rows - "Conall's
		// Valley" is both 508 and 1010, "Old Tarantia" both 2000 and 2002, "Wild Lands of Zelata" three times - and
		// in every case the harvested truth is the one that defines respawn points; the twins define nothing at all.
		// So a playfield with no spawn point cannot be a door's destination while a same-named one has them.
		// Exactly one survivor is the destination; a genuine tie (Outflow Tunnels 2026/2027) stays unresolved and
		// the server surfaces "no known destination yet" rather than guessing a coin flip.
		public static uint? PickDestination(IReadOnlyList<MapCandidate> candidates)
		{
			if (candidates.Count == 0)
				return null;

			var landable = candidates.Where(c => c.HasSpawnPoint).ToList();
			var pool = landable.Count > 0
				? landable
				: candidates;

			return pool.Count == 1
				? pool[0].MapId
				: null;
		}
	}
}
