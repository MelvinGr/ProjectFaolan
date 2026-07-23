using System;
using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Faolan.Tests
{
	// The packet TRACE (Gitea #406 phase 6): one tracer per channel, rendering BOTH directions.
	//
	// Two things are pinned here. (1) A rendered line carries the frame's REAL fields, checked against an INDEPENDENT
	// parse of the serialized bytes - the TX path renders straight from the Rpc (no re-parse), so a wrong field
	// extraction has to fail against something other than itself. (2) RX and TX of the same frame render IDENTICALLY
	// apart from the direction tag: same name, same lengths, same byte dump - which is what "unified" has to mean, and
	// is only true because both directions go through ConanPacketTracer.Trace.
	public class PacketTraceTests
	{
		private static readonly ProtoEndpoint Sender = Endpoints.Endpoint(Endpoints.GameAgentType, 0x58);
		private static readonly ProtoEndpoint Receiver = Endpoints.Endpoint(Endpoints.GameInterfaceType, 0x0012D687);

		public static IEnumerable<object[]> Cases()
		{
			// A PassBlob (IIR) carrier: [u32 payloadLen][u32 subOpcode][payload] - exercises the sub-opcode branch.
			yield return
			[
				new Rpc(Sender, Receiver, GameInterfaceOpcodes.DispatchPassBlob,
					(FaolanStream body) => body.WriteArrayUInt32Length(s => s.WriteUInt32((uint)GameServer0X2000Opcodes.StatUpdateIIR).WriteUInt32(0x0000C350).WriteUInt32(0x0012D687)))
			];

			// A non-PassBlob carrier with a plain byte body (no sub-opcode branch).
			yield return
			[
				new Rpc(Sender, Endpoints.Endpoint(Endpoints.GameCharInterfaceType, 0x0012D687), GameCharInterfaceOpcodes.DispatchUpdateCash,
					(byte[]) [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x10, 0x00, 0x00, 0x00, 0x01])
			];

			// A carrier that also carries interface HeaderData after the opcode.
			yield return
			[
				new Rpc(Sender, Receiver, GameInterfaceOpcodes.DispatchPassBlob, (byte[]) [0xDE, 0xAD, 0xBE, 0xEF],
					(byte[]) [0x00, 0x00, 0x00, 0x08, 0x11, 0x22, 0x33, 0x44])
			];

			// An empty-body carrier.
			yield return [new Rpc(Sender, Receiver, GameInterfaceOpcodes.DispatchPassBlob, ReadOnlyMemory<byte>.Empty)];
		}

		[Theory]
		[MemberData(nameof(Cases))]
		public void Transmitted_RendersTheFramesRealFields(Rpc rpc)
		{
			WithByteDump(() =>
			{
				var capture = new CapturingLogger();
				var tracer = Tracer(capture);

				var wire = PacketSerializer.Serialize(rpc);
				var parsed = PacketParser.Parse(wire); // the independent oracle: parse the serialized frame back

				tracer.Transmitted(in rpc, wire.Length);
				var line = Assert.Single(capture.Messages);

				// The rendered line must carry the frame's real fields (opcode, lengths, the header/body hex), each read
				// from the independently-parsed packet - never from the Rpc the render used.
				Assert.Contains(parsed.Opcode.ToHex(), line);
				Assert.Contains($"bodyLength={parsed.Body.Length}", line);
				Assert.Contains($"packetLength={wire.Length}", line);
				if (parsed.Body.Length > 0)
					Assert.Contains(parsed.Body.ToHex(), line);

				if (parsed.HeaderData.Length > 0)
					Assert.Contains(parsed.HeaderData.ToHex(), line);
			});
		}

		// THE unification gate: the same frame traced in both directions differs only by its direction tag. Naming,
		// sub-opcode handling, lengths and the byte dump all come from the one render, so they cannot drift per
		// direction (they used to: RX named through a different call path and dumped no headerData at all).
		[Theory]
		[MemberData(nameof(Cases))]
		public void ReceivedAndTransmitted_RenderTheSameLineApartFromTheDirectionTag(Rpc rpc)
		{
			WithByteDump(() =>
			{
				var capture = new CapturingLogger();
				var tracer = Tracer(capture);

				var wire = PacketSerializer.Serialize(rpc);
				var inbound = PacketParser.Parse(wire); // an RX-shaped Rpc: FrameLength stamped by the parser

				tracer.Transmitted(in rpc, wire.Length);
				tracer.Received(in inbound);

				Assert.Equal(2, capture.Messages.Count);
				Assert.StartsWith($"{PacketLogFormatter.Transmitted} ", capture.Messages[0]);
				Assert.StartsWith($"{PacketLogFormatter.Received} ", capture.Messages[1]);
				Assert.Equal(capture.Messages[0][PacketLogFormatter.Transmitted.Length..], capture.Messages[1][PacketLogFormatter.Received.Length..]);
			});
		}

		// A PassBlob carrier names the IIR it carries - in BOTH directions. The inbound trace used to drop it, so an
		// inbound 0x2000 read as a bare "PassBlob" with no hint of which IIR the client had sent.
		[Fact]
		public void PassBlobCarrier_NamesItsIir_InBothDirections()
		{
			var capture = new CapturingLogger();
			var tracer = Tracer(capture);

			var rpc = new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58), Endpoints.Endpoint(Endpoints.GameAgentType, 0x0012D687), GameAgentOpcodes.PassBlob,
				(FaolanStream body) => body.WriteArrayUInt32Length(s => s.WriteUInt32((uint)GameServer0X2000Opcodes.MoveTaskIIR).WriteUInt32(0x11223344)));

			tracer.Received(PacketParser.Parse(PacketSerializer.Serialize(rpc)));
			tracer.Transmitted(in rpc, PacketSerializer.Serialize(rpc).Length);

			Assert.All(capture.Messages, line =>
			{
				Assert.Contains($"subOpcode={nameof(GameServer0X2000Opcodes.MoveTaskIIR)}", line);
				Assert.Contains("PassBlob (0x2000)", line);
			});
		}

		// The IIR sub-opcode is a property of the GameAgent/GameInterface class pair, NOT of the number 0x2000: on every
		// other RPC class 0x2000 is a different method whose body would be misread as an [payloadLength][iir] header.
		[Fact]
		public void Opcode0x2000_OnAnotherClass_IsNotReadAsAnIirCarrier()
		{
			var capture = new CapturingLogger();
			var tracer = Tracer(capture);

			var rpc = new Rpc(Sender, Endpoints.Endpoint(Endpoints.GameCharInterfaceType, 0x0012D687), GameCharInterfaceOpcodes.DispatchUpdateAggroStatus,
				(byte[]) [0x00, 0x00, 0x00, 0x08, 0x11, 0x22, 0x33, 0x44]);

			tracer.Transmitted(in rpc, PacketSerializer.Serialize(rpc).Length);

			var line = Assert.Single(capture.Messages);
			Assert.Contains("DispatchUpdateAggroStatus (0x2000)", line);
			Assert.DoesNotContain("subOpcode=", line);
		}

		private static ConanPacketTracer Tracer(ILogger logger)
		{
			return new ConanPacketTracer(logger, "GameServer", typeof(GameCharInterfaceOpcodes), typeof(GameServer0X2000Opcodes));
		}

		// Renders with the headerData/body hex included, so the whole line is under test.
		private static void WithByteDump(Action body)
		{
			var previousIncludeBytes = PacketLogOptions.IncludeBytes;
			PacketLogOptions.IncludeBytes = true;
			try
			{
				body();
			}
			finally
			{
				PacketLogOptions.IncludeBytes = previousIncludeBytes;
			}
		}

		// Minimal ILogger that records each entry's fully-rendered message. IsEnabled(Debug) is true so the byte-dump
		// suffix (PacketLogOptions.BytesSuffix) is exercised.
		private sealed class CapturingLogger : ILogger
		{
			public List<string> Messages { get; } = [];

			public IDisposable BeginScope<TState>(TState state) where TState : notnull
			{
				return NullScope.Instance;
			}

			public bool IsEnabled(LogLevel logLevel)
			{
				return true;
			}

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				Messages.Add(formatter(state, exception));
			}

			private sealed class NullScope : IDisposable
			{
				public static readonly NullScope Instance = new();

				public void Dispose()
				{
				}
			}
		}
	}
}
