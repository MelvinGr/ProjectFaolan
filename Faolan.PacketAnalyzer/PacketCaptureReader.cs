using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.PacketAnalyzer.Models;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Faolan.PacketAnalyzer
{
	public sealed class PacketCaptureReader
	{
		private static readonly byte[] CompressionHeaderPrefix = [0x80, 0x00, 0x00, 0x05];
		private readonly Dictionary<string, GameServerCompressionState> _compressionStates = [];
		private readonly IReadOnlyDictionary<string, string>? _endpointProtocols;
		private readonly List<AnalyzedPacket> _packets = [];
		private readonly PacketAnalyzerPortSettings _portSettings;

		private readonly PacketCaptureScanner _captureScanner = new PacketCaptureScanner();

		private PacketProtocolDetector _protocolDetector;

		public PacketCaptureReader(PacketAnalyzerPortSettings? portSettings = null, IReadOnlyDictionary<string, string>? endpointProtocols = null)
		{
			_portSettings = portSettings ?? PacketAnalyzerPortSettings.Default;
			_endpointProtocols = endpointProtocols;
			_protocolDetector = new PacketProtocolDetector(_portSettings, endpointProtocols: _endpointProtocols);
		}

		public IReadOnlyList<AnalyzedPacket> ReadDump(string path)
		{
			_packets.Clear();
			_compressionStates.Clear();
			_protocolDetector = new PacketProtocolDetector(_portSettings, endpointProtocols: _endpointProtocols);

			_captureScanner.Process(path, ShouldInspectPacket, TransformPayload, OnPacketReceived, ResolveFramingMode);

			return _packets.ToArray();
		}

		private byte[]? DecompressFlowBytes(string flowKey, GameServerCompressionState state, byte[] bytes)
		{
			var inflater = state.Inflater;
			if (inflater == null)
				return bytes;

			var decompressedBytes = Decompress(inflater, bytes);
			if (decompressedBytes != null)
				return decompressedBytes;

			_compressionStates.Remove(flowKey);
			return null;
		}

		private GameServerCompressionState GetCompressionState(string flowKey)
		{
			if (_compressionStates.TryGetValue(flowKey, out var state))
				return state;

			state = new GameServerCompressionState();
			_compressionStates[flowKey] = state;
			return state;
		}

		private void OnPacketReceived(PacketCaptureContext context, string ticks, Packet packet)
		{
			var detectedService = packet is ConanPacket conanPacket
				? _protocolDetector.ObservePacket(context.SourceAddress, context.DestinationAddress, context.SourceIpAddress, context.DestinationIpAddress, context.SourcePort, context.DestinationPort, context.IsSourceLocal,
					context.IsDestinationLocal, context.IsDownload, conanPacket)
				: _protocolDetector.ResolveService(context.SourceAddress, context.DestinationAddress, context.SourceIpAddress, context.DestinationIpAddress, context.SourcePort, context.DestinationPort, context.IsSourceLocal,
					context.IsDestinationLocal, context.IsDownload);

			_packets.Add(AnalyzedPacket.FromPacket(ticks, detectedService.ServiceName, detectedService.ProtocolName, context.IsDownload, packet, context.SourceName, context.SourceAddress, context.DestinationAddress,
				context.SourceIpAddress, context.DestinationIpAddress, context.SourcePort, context.DestinationPort));
		}

		// The AgentServer speaks inline-framed packets ([u16 opcode][u16 length][body]) in BOTH directions, unlike
		// the length-prefixed ConanPacket servers. Without this its server->client stream is read as length-prefixed
		// and silently dropped (the first 0x003C channel packet is mistaken for a multi-megabyte length). Key off the
		// detected AgentServer port so both the client Hello and the server's channel/char-info/welcome burst parse.
		private PacketFramingMode ResolveFramingMode(PacketCaptureContext context)
		{
			var agentServerPort = _portSettings.AgentServerPort;
			return agentServerPort != 0 && (context.SourcePort == agentServerPort || context.DestinationPort == agentServerPort)
				? PacketFramingMode.AgentServerInline
				: PacketFramingMode.LengthPrefixed;
		}

		private bool ShouldInspectPacket(PacketCaptureContext context)
		{
			return _protocolDetector.ShouldInspect(context.SourceIpAddress, context.SourcePort, context.DestinationIpAddress, context.DestinationPort);
		}

		private byte[]? TransformPayload(PacketCaptureContext context, byte[] bytes)
		{
			// Detect GameServer downloads by CONTENT (the 0x80000005 zlib-stream marker), not by port. The world
			// GameServer moves to a fresh port on every playfield change, and the same port number can be a
			// different service on another server IP, so port classification mislabels these streams and the
			// importer drops them. Any server->client (download) flow is offered to the marker detector; non-GS
			// flows lack the marker and pass through untouched.
			if (context.IsSourceLocal || bytes.Length == 0)
				return bytes;

			var state = GetCompressionState(context.FlowKey);

			if (state.Inflater != null)
				return DecompressFlowBytes(context.FlowKey, state, bytes);

			if (state.PendingHeaderCount == 0 && !CouldStartCompressionHeader(bytes))
				return bytes;

			var result = TryProcessCompressionHeader(context.FlowKey, state, bytes);

			// Once the compression header is confirmed and inflation begins, this endpoint IS a GameServer:
			// relabel it (endpoint-scoped + authoritative) so its packets are kept/labeled as GameServer.
			if (state.Inflater != null)
				_protocolDetector.MarkGameServerEndpoint(context.SourceIpAddress, context.SourcePort);

			return result;
		}

		private byte[]? TryProcessCompressionHeader(string flowKey, GameServerCompressionState state, byte[] bytes)
		{
			var consumed = state.AppendHeaderBytes(bytes);

			// Early reject: once the first 4 bytes are known and they aren't the 0x80000005 marker, this isn't a
			// GameServer compression header - pass the buffered bytes through unchanged.
			if (state.PendingHeaderCount >= CompressionHeaderPrefix.Length && !state.HasCompressionHeaderPrefix)
				return FlushHeaderPassthrough(state, bytes, consumed);

			// Need the 9-byte marker header PLUS the 2-byte zlib header to validate (see below).
			if (state.PendingHeaderCount < GameServerCompressionState.HeaderLength)
				return null;

			var remainingBytes = bytes.Length > consumed
				? bytes[consumed..]
				: [];

			// Content validation: a real GameServer stream is 0x80000005 + a 5-byte framing tail, then a zlib
			// stream (CM=8, header checksum %31==0). The 0x80000005 byte sequence also occurs incidentally inside
			// uncompressed auth traffic at a reassembly-chunk boundary; without this check those flows would be
			// misdetected as GameServer and their data corrupted by a bogus inflate. If the zlib header isn't
			// valid, treat the marker as coincidental and pass everything through.
			var cmf = state.PendingHeaderBuffer[GameServerCompressionState.MarkerLength];
			var flg = state.PendingHeaderBuffer[GameServerCompressionState.MarkerLength + 1];
			if (!IsValidZlibHeader(cmf, flg))
				return FlushHeaderPassthrough(state, bytes, consumed);

			// Confirmed GameServer: feed the 2 buffered zlib-header bytes plus the rest to a fresh inflater.
			state.Inflater = new Inflater();
			state.ResetHeader();

			var zlibBytes = new byte[2 + remainingBytes.Length];
			zlibBytes[0] = cmf;
			zlibBytes[1] = flg;
			if (remainingBytes.Length > 0)
				Array.Copy(remainingBytes, 0, zlibBytes, 2, remainingBytes.Length);

			return DecompressFlowBytes(flowKey, state, zlibBytes);
		}

		private static bool CouldStartCompressionHeader(byte[] bytes)
		{
			var lengthToCompare = Math.Min(bytes.Length, CompressionHeaderPrefix.Length);
			for (var i = 0; i < lengthToCompare; i++)
			{
				if (bytes[i] != CompressionHeaderPrefix[i])
					return false;
			}

			return true;
		}

		private static byte[]? Decompress(Inflater inflater, byte[] input)
		{
			try
			{
				inflater.SetInput(input);
				using var bos = new MemoryStream(input.Length);
				var buf = new byte[10240];
				while (!inflater.IsFinished && !inflater.IsNeedingInput)
				{
					var count = inflater.Inflate(buf);
					bos.Write(buf, 0, count);
				}

				return bos.ToArray();
			}
			catch
			{
				return null;
			}
		}

		// Returns the buffered header bytes + the rest of this chunk unchanged (the marker was incomplete or
		// coincidental), and clears the header buffer.
		private static byte[] FlushHeaderPassthrough(GameServerCompressionState state, byte[] bytes, int consumed)
		{
			var remainingBytes = bytes.Length > consumed
				? bytes[consumed..]
				: [];
			var passthroughBytes = new byte[state.PendingHeaderCount + remainingBytes.Length];
			Array.Copy(state.PendingHeaderBuffer, 0, passthroughBytes, 0, state.PendingHeaderCount);
			if (remainingBytes.Length > 0)
				Array.Copy(remainingBytes, 0, passthroughBytes, state.PendingHeaderCount, remainingBytes.Length);

			state.ResetHeader();
			return passthroughBytes;
		}

		// A valid zlib header: low nibble of CMF is 8 (deflate) and the 16-bit (CMF<<8 | FLG) is a multiple of 31.
		private static bool IsValidZlibHeader(byte cmf, byte flg)
		{
			return (cmf & 0x0F) == 8 && ((cmf << 8) | flg) % 31 == 0;
		}

		private sealed class GameServerCompressionState
		{
			public const int HeaderLength = MarkerLength + 2;

			// The 0x80000005 marker + 5-byte framing tail. The 2 bytes after it are the zlib header, which we also
			// buffer (HeaderLength) so the stream can be validated as real zlib before committing to inflation.
			public const int MarkerLength = 9;

			public bool HasCompressionHeaderPrefix =>
				PendingHeaderCount >= CompressionHeaderPrefix.Length && PendingHeaderBuffer[0] == CompressionHeaderPrefix[0] && PendingHeaderBuffer[1] == CompressionHeaderPrefix[1] &&
				PendingHeaderBuffer[2] == CompressionHeaderPrefix[2] && PendingHeaderBuffer[3] == CompressionHeaderPrefix[3];

			public byte[] PendingHeaderBuffer { get; } = new byte[HeaderLength];

			public Inflater? Inflater { get; set; }
			public int PendingHeaderCount { get; private set; }

			public int AppendHeaderBytes(byte[] bytes)
			{
				var bytesToCopy = Math.Min(HeaderLength - PendingHeaderCount, bytes.Length);
				Array.Copy(bytes, 0, PendingHeaderBuffer, PendingHeaderCount, bytesToCopy);
				PendingHeaderCount += bytesToCopy;
				return bytesToCopy;
			}

			public void ResetHeader()
			{
				PendingHeaderCount = 0;
			}
		}
	}
}
