using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Extensions
{
	// ReSharper disable once InconsistentNaming
	public static class IConfigurationExtensions
	{
		extension(IConfiguration config)
		{
			// Whether to host the in-process MCP server (Claude tooling) and the in-game Claude chat channel it
			// bridges. Defaults to enabled when unset; set "McpEnabled": false in faolan.json to disable both.
			public bool McpEnabled => !bool.TryParse(config["McpEnabled"], out var enabled) || enabled;

			public string DatabasePath => Path.GetFullPath(config["DatabasePath"]);

			// Dev visualization (#363): when true, an NPC that stands inside an RDB spawn AREA gets that area's group
			// name appended to its overhead nameplate (e.g. "Jungle Ape [Ape2]"), so a manual creature-labeling pass can
			// see which area it's standing in. Off by default; set "DebugSpawnAreaLabels": true in faolan.json.
			public bool DebugSpawnAreaLabels => bool.TryParse(config["DebugSpawnAreaLabels"], out var on) && on;

			public string ServerIpAddress => config["ServerIpAddress"];

			public string WelcomeString => config["WelcomeString"];

			public uint ServerIpAddressLong => BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(config.ServerIpAddress).GetAddressBytes());

			// #324: the realm-local service ports (world/chat/guild) live on the Realm row, not global config - the
			// realm host binds them from its Realm row. Only the shared login tier's ports stay in config below.
			public ushort McpPort => Convert.ToUInt16(config["McpPort"]);

			public ushort PlayerAgentPort => Convert.ToUInt16(config["PlayerAgentPort"]);
			public ushort UniverseAgentPort => Convert.ToUInt16(config["UniverseAgentPort"]);
		}
	}
}
