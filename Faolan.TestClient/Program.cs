using System.Security.Cryptography;
using System.Text;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.TestClient;

// Faolan.TestClient — a minimal console client that mirrors the retail client's protocol from login through to the
// character-select screen, WITHOUT the retail exe. It speaks the real wire protocol (reusing the server's own
// PacketStream / ConanMessageFramer / ConanPacket) and does REAL login crypto: Diffie-Hellman + TEA-CBC via
// LoginEncryption.BuildClientLoginKey (the "patched client" path), so the server enforces the account password.
//
// Flow:  UniverseAgent(7010): InitiateAuthentication -> DispatchChallenge -> AnswerChallenge(DH+TEA) ->
//        DispatchAckAuthenticate(realm handoff) ;  PlayerAgent: RequestAuth -> RequestSmallCharList -> the char list.
//
// Usage: dotnet run --project Faolan.TestClient -- [--host 127.0.0.1] [--port 7010] --user <acct> --pass <password>

var opt = ParseArgs(args);
Console.WriteLine($"Faolan test client -> {opt.Host}:{opt.Port}  user={opt.User}");

// The client picks its own conversation-id endpoint; the server echoes it back as the receiver on every reply.
var clientEndpoint = ConanEndpoints.EndpointBytes(0x00000001, 1);
byte[] Server(uint type) => ConanEndpoints.EndpointBytes(type, 1);

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

await universe.SendAsync(Build(Server(0x60dba0), UniverseAgentOpcodes.InitiateAuthentication, p =>
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

await player.SendAsync(Build(Server(0x8204), PlayerAgentOpcodes.RequestAuth, p =>
{
	p.WriteUInt32(accountId);
	p.WriteUInt32(0);          // cookie (unused for the account lookup)
	p.WriteUInt32(authStatus);
}));
Console.WriteLine("[player] -> RequestAuth");

await player.ReadUntilAsync((ushort)PlayerAgentResponseOpcodes.InitAuth, timeout, Log);
Console.WriteLine("[player] <- InitAuth");

await player.SendAsync(Build(Server(0x8204), PlayerAgentOpcodes.RequestSmallCharList, p => p.WriteUInt16(0)));
Console.WriteLine("[player] -> RequestSmallCharList");

var listPacket = await player.ReadUntilAsync((ushort)PlayerAgentResponseOpcodes.SmallCharList, timeout, Log);
var count = listPacket.Body.ReadUInt32() / 1009 - 1; // (count+1)*1009 framing
Console.WriteLine();
Console.WriteLine($"===== CHARACTER SELECT — {count} character(s) =====");
for (var i = 0; i < count; i++)
{
	var id = listPacket.Body.ReadUInt32();
	var authChallenge = listPacket.Body.ReadString();
	Console.WriteLine($"  [{i}] characterId=0x{id:X8} ({id})  authChallenge=\"{authChallenge}\"");
}

Console.WriteLine();
Console.WriteLine("Reached the character-select screen. (Names/appearance need RequestCharRealmData -> SendCharacterList.)");
return 0;

static void Log(ConanPacket p) => Console.WriteLine($"      (skipped 0x{p.Opcode:X4}, {p.Body.Length} body bytes)");

static (string Host, int Port) SplitAddr(string addr, string fallbackHost)
{
	var i = addr.LastIndexOf(':');
	return i > 0 ? (addr[..i], int.Parse(addr[(i + 1)..])) : (fallbackHost, 7011);
}

static Options ParseArgs(string[] a)
{
	var o = new Options();
	for (var i = 0; i < a.Length; i++)
		switch (a[i])
		{
			case "--host": o.Host = a[++i]; break;
			case "--port": o.Port = int.Parse(a[++i]); break;
			case "--user": o.User = a[++i]; break;
			case "--pass": o.Pass = a[++i]; break;
		}

	return o;
}

sealed class Options
{
	public string Host = "127.0.0.1";
	public int Port = 7010;
	public string User = "faolan";
	public string Pass = "";
}
