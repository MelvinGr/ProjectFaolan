using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Newtonsoft.Json;

namespace Faolan.PacketAnalyzer
{
	public class PacketWrapper : INotifyPropertyChanged
	{
		private Brush _color;

		public string Ticks { get; set; }
		public string ServiceName { get; set; }
		public bool IsDownload { get; set; }

		public string Notes { get; set; }

		[JsonIgnore]
		public ConanPacket ConanPacket { get; set; }

		[JsonIgnore]
		public string HexDisplay { get; }

		[JsonIgnore]
		public string BytesRowDisplay { get; }

		[JsonIgnore]
		public string BytesStringDisplay { get; }

		[JsonIgnore]
		public string ServiceNameDisplay { get; }

		[JsonIgnore]
		public string Opcode => ConanPacket?.Opcode.ToHex();

		[JsonIgnore]
		public string GameServerOpcode { get; }

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

		public PacketWrapper(string ticks, string serviceName, bool isDownload, ConanPacket conanPacket)
		{
			Ticks = ticks;
			ServiceName = serviceName;
			IsDownload = isDownload;
			ConanPacket = conanPacket;

			HexDisplay = Bytes?.ToHex();
			BytesRowDisplay = HexDisplay?.Substring(0, Math.Min(HexDisplay.Length, 295));
			BytesStringDisplay = Bytes?.GetPrintable();
			ServiceNameDisplay = $"{ServiceName} {(IsDownload ? "DL" : "UP")}";

			if (serviceName.Contains("gs") && conanPacket.SenderBytes[1] != 0x48 && conanPacket.Data != null)
			{
				var pbLength = conanPacket.Data.ReadUInt32();

                if (isDownload)
                {
                    var pbOpcode = conanPacket.Data.ReadUInt32<GameServer0X2000RespondsOpcodes>();
                    //Console.WriteLine("OPCODE: " + pbOpcode);
                    GameServerOpcode = Enum.IsDefined(pbOpcode) ? pbOpcode.ToString() : pbOpcode.ToHex();
                }
                else
                {
                    var pbOpcode = conanPacket.Data.ReadUInt32<GameServer0X2000Opcodes>();
                    //Console.WriteLine("OPCODE: " + pbOpcode);
                    GameServerOpcode = Enum.IsDefined(pbOpcode) ? pbOpcode.ToString() : pbOpcode.ToHex();
                }
            }
		}

		[JsonConstructor]
		public PacketWrapper(string ticks, string serviceName, bool isDownload, byte[] bytes, Brush color) 
			: this(ticks, serviceName, isDownload, new ConanPacket(bytes)) 
		{
			_color = color;
		}

		public event PropertyChangedEventHandler PropertyChanged;

		public override string ToString()
		{
			return $"({Ticks}) ({ConanPacket.Opcode:X8}) {GameServerOpcode} | {BytesRowDisplay}";
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
