using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Newtonsoft.Json;

namespace Faolan.PacketAnalyzer
{
	public class PacketWrapper : INotifyPropertyChanged
	{
		private Brush _color;

		public long Ticks { get; set; }
		public string ServiceName { get; set; }
		public bool IsDownload { get; set; }

		[JsonIgnore]
		public ConanPacket ConanPacket { get; set; }

		[JsonIgnore]
		public string Hex { get; }

		[JsonIgnore]
		public string DisplayString => ToString();

		public byte[] Bytes // for serialization
		{
			get => ConanPacket?.Bytes;
			set => ConanPacket = new ConanPacket(value);
		}

		public Brush Color
		{
			get => _color;

			set => SetField(ref _color, value);
		}

		public PacketWrapper(long ticks, string serviceName, bool isDownload, ConanPacket conanPacket)
		{
			Ticks = ticks;
			ServiceName = serviceName;
			IsDownload = isDownload;
			ConanPacket = conanPacket;
			Hex = conanPacket.Data?.ToArray().ToHex();
		}

		[JsonConstructor]
		public PacketWrapper(long ticks, string serviceName, bool isDownload, byte[] bytes, Brush color)
		{
			Ticks = ticks;
			ServiceName = serviceName;
			IsDownload = isDownload;
			ConanPacket = new ConanPacket(bytes);
			Hex = ConanPacket.Data?.ToArray().ToHex();
			_color = color;
		}

		public event PropertyChangedEventHandler PropertyChanged;

		public override string ToString()
		{
			return $"({Ticks}) ({ServiceName} {(IsDownload ? "DL" : "UP")}) ({ConanPacket.Opcode:X8}) | {Hex?.Substring(0, Math.Min(Hex.Length, 295))}";
		}

		protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
		{
			if (EqualityComparer<T>.Default.Equals(field, value))
				return false;

			field = value;
			OnPropertyChanged(propertyName);
			return true;
		}
	}
}
