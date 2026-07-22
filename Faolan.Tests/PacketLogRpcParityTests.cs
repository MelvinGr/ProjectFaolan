using System;
using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Faolan.Tests
{
	// Gitea #406 phase 6 oracle: the structured DL log path (ConanOutgoingPacketLogger.Log(in Rpc), which renders straight
	// from the Rpc model) must produce the EXACT same trace line as the historical byte[] path (Log, which re-parses
	// the serialized frame back into a ConanPacket). If the two ever drift, "no re-parse" would silently change what
	// operators see in the log - so we pin them equal for the representative frame shapes.
	public class PacketLogRpcParityTests
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
		public void LogRpc_RendersIdenticallyToTheReParsedBytePath(Rpc rpc)
		{
			var previousIncludeBytes = PacketLogOptions.IncludeBytes;
			PacketLogOptions.IncludeBytes = true; // render the headerData/body hex too, so the whole line is compared
			try
			{
				var capture = new CapturingLogger();
				var packetLogger = new ConanOutgoingPacketLogger(capture, "GameServer", typeof(GameCharInterfaceOpcodes), typeof(GameServer0X2000Opcodes));
				var wire = PacketSerializer.Serialize(rpc);

				capture.Messages.Clear();
				packetLogger.Log(wire); // the historical path: re-parse the bytes
				var reParsed = Assert.Single(capture.Messages);

				capture.Messages.Clear();
				packetLogger.Log(in rpc, wire.Length); // phase 6: render from the Rpc model
				var structured = Assert.Single(capture.Messages);

				Assert.Equal(reParsed, structured);
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
