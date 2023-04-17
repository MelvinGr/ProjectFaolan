using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Conan;
using Faolan.PacketAnalyzer.Models;
using Brush = object;

namespace Faolan.PacketAnalyzer.ViewModels
{
	public class PacketRowViewModel : INotifyPropertyChanged
	{
		private Brush? _color;
		private string? _notes;

		public AnalyzedPacket Packet { get; }
		public bool IsDownload => Packet.IsDownload;

		public Brush? Color
		{
			get => _color;
			set => SetField(ref _color, value);
		}

		public byte[] Bytes => Packet.Bytes;

		public ConanPacket? ConanPacket => Packet.ConanPacket;
		public string BytesAscii => Packet.Bytes.ToAsciiRepresentation();
		public string BytesRowDisplay => HexDisplay.Substring(0, Math.Min(HexDisplay.Length, 295));
		public string BytesStringDisplay => Packet.Bytes.GetPrintable();
		public string DestinationAddress => Packet.DestinationAddress;
		public string DestinationIpAddress => Packet.DestinationIpAddress;
		public string DirectionName => PacketProtocolInfo.GetDirectionName(IsDownload);

		public string EndpointDisplay =>
			!string.IsNullOrWhiteSpace(Packet.SourceAddress) && !string.IsNullOrWhiteSpace(Packet.DestinationAddress)
				? $"{Packet.SourceAddress} → {Packet.DestinationAddress}"
				: string.Empty;

		public string HexDisplay => Packet.Bytes.ToHex();

		public string LengthDisplay =>
			PayloadLength.HasValue
				? $"{PacketLength} / {PayloadLength.Value}"
				: PacketLength.ToString();

		public string Opcode => PacketProtocolInfo.GetOpcodeDisplay(ProtocolName, IsDownload, OpcodeValue);
		public string OpcodeHex => Packet.OpcodeHex;
		public string ProtocolName => Packet.ProtocolName;
		public string ServiceName => Packet.ServiceName;

		public string ServiceNameDisplay =>
			string.IsNullOrWhiteSpace(ProtocolName) || string.Equals(ProtocolName, PacketProtocolInfo.UnknownProtocolName, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(ProtocolName, ServiceName, StringComparison.OrdinalIgnoreCase)
				? $"{ServiceName} {(IsDownload ? "DL" : "UP")}"
				: $"{ProtocolName} [{ServiceName}] {(IsDownload ? "DL" : "UP")}";

		public string SourceAddress => Packet.SourceAddress;
		public string SourceIpAddress => Packet.SourceIpAddress;

		public string SourceName => Packet.SourceName;
		public string Ticks => Packet.Ticks;

		public string? GameServerOpcode =>
			SubOpcodeValue.HasValue
				? !string.IsNullOrWhiteSpace(SubOpcodeName)
					? $"{SubOpcodeName} ({SubOpcodeHex})"
					: SubOpcodeHex
				: null;

		public string? Notes
		{
			get => _notes;
			set => SetField(ref _notes, value);
		}

		public string? SubOpcodeHex =>
			string.IsNullOrWhiteSpace(Packet.SubOpcodeHex)
				? null
				: Packet.SubOpcodeHex;

		public string? SubOpcodeName =>
			string.IsNullOrWhiteSpace(Packet.SubOpcodeName)
				? null
				: Packet.SubOpcodeName;

		public uint OpcodeValue => Packet.OpcodeValue;

		public uint PacketLength => Packet.PacketLength;
		public uint? PayloadLength => Packet.PayloadLength;
		public uint? SubOpcodeValue => Packet.SubOpcodeValue;
		public ushort DestinationPort => Packet.DestinationPort;
		public ushort SourcePort => Packet.SourcePort;

		public PacketRowViewModel(AnalyzedPacket packet)
		{
			Packet = packet;
		}

		public override string ToString()
		{
			return $"({Ticks}) ({OpcodeValue:X8}) {GameServerOpcode} | {BytesRowDisplay}";
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
		{
			if (EqualityComparer<T>.Default.Equals(field, value))
				return false;

			field = value;
			OnPropertyChanged(propertyName);
			return true;
		}
	}
}
