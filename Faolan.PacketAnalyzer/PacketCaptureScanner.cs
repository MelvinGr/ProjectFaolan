using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using CorePacket = Faolan.Core.Network.Packets.Packet;

namespace Faolan.PacketAnalyzer
{
	internal sealed class PacketCaptureScanner
	{
		private readonly Dictionary<string, PacketSplitter> _packetSplitters = new Dictionary<string, PacketSplitter>();
		private readonly Dictionary<string, TcpStreamReassembler> _reassemblers = new Dictionary<string, TcpStreamReassembler>();

		public void Process(string path, Func<PacketCaptureContext, bool> shouldInspect, Func<PacketCaptureContext, byte[], byte[]?> transformPayload, Action<PacketCaptureContext, string, CorePacket> onPacket,
			Func<PacketCaptureContext, PacketFramingMode>? getFramingMode = null)
		{
			if (string.IsNullOrWhiteSpace(path))
				throw new ArgumentException("A capture file path is required.", nameof(path));

			if (!File.Exists(path))
				throw new FileNotFoundException("Capture file was not found.", path);

			_packetSplitters.Clear();
			_reassemblers.Clear();
			var captureCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using var captureFileReaderDevice = new CaptureFileReaderDevice(path);
			captureFileReaderDevice.OnPacketArrival += (_, capture) => OnPacketArrival(path, capture, shouldInspect, transformPayload, onPacket, getFramingMode);
			CaptureStoppedEventHandler captureStopped = (_, _) => captureCompleted.TrySetResult(true);
			captureFileReaderDevice.OnCaptureStopped += captureStopped;

			try
			{
				captureFileReaderDevice.Open();
				captureFileReaderDevice.StartCapture();
				captureCompleted.Task.GetAwaiter().GetResult();
			}
			finally
			{
				captureFileReaderDevice.OnCaptureStopped -= captureStopped;
			}
		}

		private void OnPacketArrival(string path, PacketCapture capture, Func<PacketCaptureContext, bool> shouldInspect, Func<PacketCaptureContext, byte[], byte[]?> transformPayload,
			Action<PacketCaptureContext, string, CorePacket> onPacket, Func<PacketCaptureContext, PacketFramingMode>? getFramingMode)
		{
			var rawCapture = capture.GetPacket();
			var ethernetPacket = rawCapture.GetPacket();
			var ipPacket = ethernetPacket.PayloadPacket as IPPacket;
			if (ipPacket?.PayloadPacket is not TcpPacket tcpPacket)
				return;

			var sourceIpAddress = ipPacket.SourceAddress.ToString();
			var destinationIpAddress = ipPacket.DestinationAddress.ToString();
			var isSourceLocal = ReverseDnsResolver.IsLocalAddress(ipPacket.SourceAddress);
			var isDestinationLocal = ReverseDnsResolver.IsLocalAddress(ipPacket.DestinationAddress);
			var sourceAddress = sourceIpAddress + $":{tcpPacket.SourcePort}";
			var destinationAddress = destinationIpAddress + $":{tcpPacket.DestinationPort}";
			var context = new PacketCaptureContext(Path.GetFileName(path), sourceAddress, destinationAddress, sourceIpAddress, destinationIpAddress, tcpPacket.SourcePort, tcpPacket.DestinationPort, isSourceLocal,
				isDestinationLocal, capture.Header.Timeval.Date.Ticks);

			if (!shouldInspect(context))
				return;

			var flowKey = context.FlowKey;
			if (!_packetSplitters.TryGetValue(flowKey, out var packetSplitter))
			{
				_packetSplitters[flowKey] = packetSplitter = new PacketSplitter(flowKey, getFramingMode?.Invoke(context) ?? PacketFramingMode.LengthPrefixed)
				{
					ReceivedPacket = (ticks, packet) => onPacket(context, ticks, packet)
				};
			}

			var rawPayload = tcpPacket.PayloadData;
			if (rawPayload == null || rawPayload.Length == 0)
				return;

			// Reassemble the TCP stream per flow before doing anything with the payload: the GameServer
			// download is a single zlib stream, so a retransmitted or out-of-order segment fed to the
			// inflater corrupts it and the rest of the stream is lost. Order by sequence number, drop
			// already-seen bytes, and buffer gaps until they fill.
			if (!_reassemblers.TryGetValue(flowKey, out var reassembler))
				_reassemblers[flowKey] = reassembler = new TcpStreamReassembler();

			var orderedBytes = reassembler.Push(tcpPacket.SequenceNumber, rawPayload);
			if (orderedBytes.Length == 0)
				return;

			var bytes = transformPayload(context, orderedBytes);
			if (bytes?.Length > 0)
				packetSplitter.DataReceived(context.Ticks, bytes);
		}
	}

	// Minimal in-order TCP stream reassembler for a single directional flow: delivers contiguous bytes in
	// sequence order, drops retransmitted/overlapping data, and buffers out-of-order segments until the gap
	// fills. (Sequence comparisons use signed 32-bit difference so they survive wraparound.)
	internal sealed class TcpStreamReassembler
	{
		private readonly SortedDictionary<uint, byte[]> _pending = new SortedDictionary<uint, byte[]>();
		private bool _initialized;
		private uint _nextSequence;

		public byte[] Push(uint sequence, byte[] payload)
		{
			if (payload.Length == 0)
				return [];

			if (!_initialized)
			{
				_initialized = true;
				_nextSequence = sequence;
			}

			using var output = new MemoryStream(payload.Length);
			Accept(sequence, payload, output);
			Drain(output);
			return output.ToArray();
		}

		private void Accept(uint sequence, byte[] payload, MemoryStream output)
		{
			var diff = (int)(sequence - _nextSequence);

			if (diff == 0)
			{
				output.Write(payload, 0, payload.Length);
				_nextSequence += (uint)payload.Length;
			}
			else if (diff < 0)
			{
				// Starts before the next expected byte: a retransmit, possibly overlapping into new data.
				var overlap = (int)(_nextSequence - sequence);
				if (overlap < payload.Length)
				{
					output.Write(payload, overlap, payload.Length - overlap);
					_nextSequence += (uint)(payload.Length - overlap);
				}
			}
			else
			{
				// Future segment (gap before it): buffer until the gap fills, keeping the longest at this seq.
				if (!_pending.TryGetValue(sequence, out var existing) || existing.Length < payload.Length)
					_pending[sequence] = payload;
			}
		}

		private void Drain(MemoryStream output)
		{
			while (_pending.Count > 0)
			{
				uint? next = null;
				foreach (var sequence in _pending.Keys)
				{
					if ((int)(sequence - _nextSequence) <= 0)
					{
						next = sequence;
						break;
					}
				}

				if (next == null)
					break;

				var payload = _pending[next.Value];
				_pending.Remove(next.Value);
				Accept(next.Value, payload, output);
			}
		}
	}

	internal readonly record struct PacketCaptureContext(
		string SourceName,
		string SourceAddress,
		string DestinationAddress,
		string SourceIpAddress,
		string DestinationIpAddress,
		ushort SourcePort,
		ushort DestinationPort,
		bool IsSourceLocal,
		bool IsDestinationLocal,
		long Ticks)
	{
		public bool IsDownload => IsDestinationLocal;
		public string FlowKey => $"{SourceAddress} > {DestinationAddress}";
	}
}
