using System.Security.Cryptography;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.FullCharSnapshot;
using Faolan.TestClient;

// Faolan.TestClient — a console client that mirrors the retail client's protocol from login through WORLD ENTRY,
// WITHOUT the retail exe. It speaks the real wire protocol (reusing the server's own PacketStream /
// ConanMessageFramer / ConanPacket) and does REAL login crypto: Diffie-Hellman + TEA-CBC via
// LoginEncryption.BuildClientLoginKey (the "patched client" path), so the server enforces the account password.
//
// Flow:  UniverseAgent(7010): InitiateAuthentication -> DispatchChallenge -> AnswerChallenge(DH+TEA) ->
//          DispatchAckAuthenticate(realm handoff)
//        PlayerAgent:          RequestAuth -> RequestSmallCharList -> RequestCharRealmData (char list w/ names) ->
//          EnterWorld -> Ox20EC -> GameServer handoff
//        GameServer:           Hello -> the world-entry burst, whose FullChar snapshot (0x737A6DF9) is decoded
//          with the server's own InventorySnapshotInspector (containers + item templates printed).
//
// Usage: dotnet run --project Faolan.TestClient -- [--host 127.0.0.1] [--port 7010] --user <acct> --pass <pw> [--char N]

var opt = ParseArgs(args);
Console.WriteLine($"Faolan test client -> {opt.Host}:{opt.Port}  user={opt.User}");

// The client picks its own conversation-id endpoint; the server echoes it back as the receiver on every reply.
var clientEndpoint = ConanEndpoints.EndpointBytes(0x00000001, 1);

byte[] Server(uint type)
{
	return ConanEndpoints.EndpointBytes(type, 1);
}

var timeout = TimeSpan.FromSeconds(10);

// Build a finalized packet (header + body + length/crc) ready for the socket.
byte[] Build<T>(byte[] receiver, T opcode, Action<PacketStream> body) where T : struct
{
	var ps = new PacketStream();
	ps.WriteHeader(clientEndpoint, receiver, opcode);
	body(ps);
	ps.WriteLengthHash();
	return ps.ToArray();
}

// ---- 1. UniverseAgent: authenticate ------------------------------------------------------------------------
using var universe = new ProtocolConnection();
await universe.ConnectAsync(opt.Host, opt.Port);
Console.WriteLine("[universe] connected");

await universe.SendAsync(Build(Server(0x60dba0),
	UniverseAgentOpcodes.InitiateAuthentication,
	p =>
	{
		p.WriteString(opt.User);
		p.WriteUInt32(0);
	}));
Console.WriteLine("[universe] -> InitiateAuthentication");

var challengePacket = await universe.ReadUntilAsync((ushort)UniverseAgentResponseOpcodes.DispatchChallenge, timeout, Log);
var challenge = challengePacket.Body.ReadString();
Console.WriteLine($"[universe] <- DispatchChallenge  challenge=\"{challenge}\"");

// REAL login block: DH keypair + TEA-CBC over username|challenge|password.
var clientPrivate = RandomNumberGenerator.GetBytes(32);
var loginKey = LoginEncryption.BuildClientLoginKey(opt.User, challenge, opt.Pass, clientPrivate);
Console.WriteLine($"[universe] built encrypted login key ({loginKey.Length} hex chars, DH+TEA-CBC)");

await universe.SendAsync(Build(Server(0x60dba0), UniverseAgentOpcodes.AnswerChallenge, p => p.WriteString(loginKey)));
Console.WriteLine("[universe] -> AnswerChallenge (encrypted)");

var ack = await universe.ReadUntilAsync((ushort)UniverseAgentResponseOpcodes.DispatchAckAuthenticate, timeout, Log);
var authStatus = ack.Body.ReadUInt32();
if (authStatus == 0xFFFFFFFF)
{
	Console.WriteLine("[universe] <- LOGIN REJECTED (invalid login / wrong password). Stopping.");
	return 1;
}

var longId = ack.Body.ReadUInt64();
var playerAgentAddr = ack.Body.ReadString();
var accountId = ack.Body.ReadUInt32();
Console.WriteLine($"[universe] <- DispatchAckAuthenticate  authStatus={authStatus} accountId=0x{accountId:X8} playerAgent={playerAgentAddr}");

// ---- 2. PlayerAgent: reach the character list --------------------------------------------------------------
var (paHost, paPort) = SplitAddr(playerAgentAddr, opt.Host);
using var player = new ProtocolConnection();
await player.ConnectAsync(paHost, paPort);
Console.WriteLine($"[player] connected -> {paHost}:{paPort}");

await player.SendAsync(Build(Server(0x8204),
	PlayerAgentOpcodes.RequestAuth,
	p =>
	{
		p.WriteUInt32(accountId);
		p.WriteUInt32(0); // cookie (unused for the account lookup)
		p.WriteUInt32(authStatus);
	}));
Console.WriteLine("[player] -> RequestAuth");

await player.ReadUntilAsync((ushort)PlayerAgentResponseOpcodes.InitAuth, timeout, Log);
Console.WriteLine("[player] <- InitAuth");

await player.SendAsync(Build(Server(0x8204), PlayerAgentOpcodes.RequestSmallCharList, p => p.WriteUInt16(0)));
Console.WriteLine("[player] -> RequestSmallCharList");

var listPacket = await player.ReadUntilAsync((ushort)PlayerAgentResponseOpcodes.SmallCharList, timeout, Log);
var count = listPacket.Body.ReadUInt32() / 1009 - 1; // (count+1)*1009 framing
var roster = new List<(uint Id, string Challenge)>();
for (var i = 0; i < count; i++)
	roster.Add((listPacket.Body.ReadUInt32(), listPacket.Body.ReadString()));
Console.WriteLine($"[player] <- SmallCharList  ({count} character id(s))");

// Ask for the full realm data by echoing the ids+challenges back; the server replies with the full character
// list (names, level, class/race/sex, map) - the actual character-select screen content.
await player.SendAsync(Build(Server(0x8204),
	PlayerAgentOpcodes.RequestCharRealmData,
	p =>
	{
		p.WriteUInt32((roster.Count + 1) * 1009);
		foreach (var (id, challenge) in roster)
		{
			p.WriteUInt32(id);
			p.WriteString(challenge);
		}
	}));
Console.WriteLine("[player] -> RequestCharRealmData");

var full = await player.ReadUntilAsync((ushort)PlayerAgentResponseOpcodes.SendCharacterList, timeout, Log);
var b = full.Body;
b.ReadUInt32(); // ClientInstance
var fullCount = b.ReadUInt32() / 1009 - 1;
var characters = new List<(uint Id, string Name)>();
Console.WriteLine();
Console.WriteLine($"===== CHARACTER SELECT — {fullCount} character(s) =====");
for (var i = 0; i < fullCount; i++)
{
	var id = b.ReadUInt32();
	b.ReadUInt32(); // PlayerInstance
	b.ReadUInt32(); // id (repeat)
	var name = b.ReadString();
	var realmId = b.ReadUInt32();
	var sex = (Sex)(byte)b.ReadUInt32();
	var lastConn = b.ReadString();
	b.ReadUInt32(); // 0
	var mapId = b.ReadUInt32();
	var level = b.ReadUInt32();
	var cls = (Class)(byte)b.ReadUInt32();
	b.ReadUInt32();
	b.ReadUInt32(); // 0, 0
	b.ReadUInt32(); // 0x60355308
	b.ReadUInt32(); // 2
	var race = (Race)(byte)b.ReadUInt32();
	b.ReadString(); // language
	b.ReadUInt32();
	b.ReadUInt32(); // 0, 0
	b.ReadString(); // per-char auth challenge
	b.ReadUInt32();
	b.ReadUInt32(); // 0, 1
	characters.Add((id, name));
	Console.WriteLine($"  [{i}] \"{name}\"  L{level} {cls} · {race} · {sex}  map={mapId} realm={realmId}  id=0x{id:X8}  last={lastConn}");
}

if (characters.Count == 0)
{
	Console.WriteLine("No characters; stopping at character-select.");
	return 0;
}

// ---- 3. EnterWorld -> GameServer handoff -------------------------------------------------------------------
var target = characters[Math.Clamp(opt.CharIndex, 0, characters.Count - 1)];
Console.WriteLine();
Console.WriteLine($"Entering world as \"{target.Name}\" (0x{target.Id:X8})...");

await player.SendAsync(Build(Server(0x8204),
	PlayerAgentOpcodes.EnterWorld,
	p =>
	{
		p.WriteUInt32(target.Id); // characterId (the only field the server uses)
		p.WriteUInt32(0);
		p.WriteUInt32(0);
		p.WriteUInt32(0);
		p.WriteByte(0);
		p.WriteUInt32(0);
		p.WriteUInt32(0);
		p.WriteUInt32(0);
	}));
Console.WriteLine("[player] -> EnterWorld");
await player.ReadUntilAsync((ushort)PlayerAgentResponseOpcodes.Ox20B9, timeout, Log);
Console.WriteLine("[player] <- Ox20B9");

// Ox20EC makes the server emit the CsPlayerAgent / AgentServer / GameServer service handoffs (all Ox1A07).
await player.SendAsync(Build(Server(0x8204), PlayerAgentOpcodes.Ox20EC, _ => { }));
Console.WriteLine("[player] -> Ox20EC");

// The GameServer handoff is the one Ox1A07 whose body is a "host:port" STRING (the others start with a u32 IP).
string? gameServerAddr = null;
var handoffDeadline = DateTime.UtcNow + timeout;
while (gameServerAddr == null && DateTime.UtcNow < handoffDeadline)
{
	var p = await player.ReadAsync(handoffDeadline - DateTime.UtcNow);
	if (p.Opcode != (ushort)PlayerAgentResponseOpcodes.Ox1A07)
		continue;

	try
	{
		var s = p.Body.ReadString();
		if (s.Contains(':') && s[(s.LastIndexOf(':') + 1)..].All(char.IsDigit))
			gameServerAddr = s;
	}
	catch
	{
		/* not the string-bodied GameServer handoff */
	}
}

if (gameServerAddr == null)
{
	Console.WriteLine("Did not receive a GameServer handoff; stopping.");
	return 1;
}

Console.WriteLine($"[player] <- GameServer handoff: {gameServerAddr}");

// ---- 4. GameServer: Hello -> world-entry burst -------------------------------------------------------------
var (gsHost, gsPort) = SplitAddr(gameServerAddr, opt.Host);
using var game = new ProtocolConnection();
await game.ConnectAsync(gsHost, gsPort);
Console.WriteLine($"[game] connected -> {gsHost}:{gsPort}");

// Existing-character login: the server sends the full world-entry burst straight after Hello.
await game.SendAsync(Build(Server(0x8204),
	GameServerOpcodes.Hello,
	p =>
	{
		p.WriteUInt32(target.Id); // clientInstance = character id
		p.WriteUInt32(accountId);
		p.WriteString("v4.00.NoTS@369764");
	}));
Console.WriteLine("[game] -> Hello");
Console.WriteLine();
Console.WriteLine("===== WORLD-ENTRY BURST =====");

var idle = TimeSpan.FromSeconds(3);
var pktCount = 0;
while (true)
{
	ConanPacket p;
	try
	{
		p = await game.ReadAsync(idle);
	}
	catch (TimeoutException)
	{
		break;
	}
	catch (IOException)
	{
		break;
	}

	pktCount++;
	if (p.Opcode == 0x2000 && TryDecodeSnapshot(p))
		continue;

	Console.WriteLine($"  pkt {pktCount,3}: opcode=0x{p.Opcode:X4}  {p.Body.Length,6} body bytes");
}

Console.WriteLine();
Console.WriteLine($"World entry reached — received {pktCount} burst packet(s).");
return 0;

// Reuse the SERVER's own decoder for the FullChar snapshot (0x737A6DF9): print each container's item count/templates.
static bool TryDecodeSnapshot(ConanPacket packet)
{
	try
	{
		var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
		if (body.ReadUInt32() != 0x737A6DF9)
			return false;

		body.ReadUInt32(); // c350
		body.ReadUInt32(); // instance
		var root = InventorySnapshotInspector.ParseInventoryPacketBody(body).Root;

		Console.WriteLine("  >>> FullChar snapshot (0x737A6DF9) — decoded via InventorySnapshotInspector:");
		foreach (InventorySnapshotInspector.ContainerKind kind in Enum.GetValues(typeof(InventorySnapshotInspector.ContainerKind)))
		{
			var container = InventorySnapshotInspector.GetContainer(root, kind);
			var entries = InventorySnapshotInspector.GetContainerEntries(container).ToList();
			if (entries.Count == 0)
				continue;

			var templates = entries.Select(e => InventorySnapshotInspector.GetEntryTemplateId(e)).Where(t => t.HasValue).Select(t => t!.Value);
			Console.WriteLine($"        {kind,-8}: {entries.Count,2} item(s)  templates=[{string.Join(", ", templates)}]");
		}

		return true;
	}
	catch
	{
		return false; // not a snapshot carrier (or a body we can't frame) - let the caller log it generically
	}
}

static void Log(ConanPacket p)
{
	Console.WriteLine($"      (skipped 0x{p.Opcode:X4}, {p.Body.Length} body bytes)");
}

static (string Host, int Port) SplitAddr(string addr, string fallbackHost)
{
	var i = addr.LastIndexOf(':');
	return i > 0
		? (addr[..i], int.Parse(addr[(i + 1)..]))
		: (fallbackHost, 7011);
}

static Options ParseArgs(string[] a)
{
	var o = new Options();
	for (var i = 0; i < a.Length; i++)
	{
		switch (a[i])
		{
			case "--host":
				o.Host = a[++i];
				break;
			case "--port":
				o.Port = int.Parse(a[++i]);
				break;
			case "--user":
				o.User = a[++i];
				break;
			case "--pass":
				o.Pass = a[++i];
				break;
			case "--char":
				o.CharIndex = int.Parse(a[++i]);
				break;
		}
	}

	return o;
}

namespace Faolan.TestClient
{
	internal sealed class Options
	{
		public int CharIndex;
		public int Port = 7010;
		public string Host = "127.0.0.1";
		public string Pass = "";
		public string User = "faolan";
	}
}
