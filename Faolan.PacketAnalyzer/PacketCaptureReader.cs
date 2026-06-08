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

			_captureScanner.Process(path, ShouldInspectPacket, TransformPayload, OnPacketReceived);

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
			if (state.PendingHeaderCount < GameServerCompressionState.HeaderLength)
				return null;

			var remainingBytes = bytes.Length > consumed
				? bytes[consumed..]
				: [];

			if (!state.HasCompressionHeaderPrefix)
			{
				var passthroughBytes = new byte[state.PendingHeaderCount + remainingBytes.Length];
				Array.Copy(state.PendingHeaderBuffer, 0, passthroughBytes, 0, state.PendingHeaderCount);
				if (remainingBytes.Length > 0)
					Array.Copy(remainingBytes, 0, passthroughBytes, state.PendingHeaderCount, remainingBytes.Length);

				state.ResetHeader();
				return passthroughBytes;
			}

			state.Inflater = new Inflater();
			state.ResetHeader();

			return remainingBytes.Length > 0
				? DecompressFlowBytes(flowKey, state, remainingBytes)
				: null;
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

		private sealed class GameServerCompressionState
		{
			public const int HeaderLength = 9;

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
