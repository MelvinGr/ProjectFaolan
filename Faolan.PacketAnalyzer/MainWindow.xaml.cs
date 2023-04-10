using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using ICSharpCode.SharpZipLib.Zip.Compression;
using Newtonsoft.Json;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Faolan.PacketAnalyzer
{
	public partial class MainWindow : Window
	{
		private readonly Dictionary<string, PacketSplitter> _packetSplitters = new();

		private CaptureFileReaderDevice _captureFileReaderDevice;
		private Inflater _inflater;


		public MainWindow()
		{
			InitializeComponent();

			outputListBox.ContextMenu = new ContextMenu();

			var colors = typeof(Colors)
				.GetProperties(BindingFlags.Static | BindingFlags.Public)
				.Where(p => p.Name.StartsWith("Light"))
				.ToDictionary(p => p.Name, v => new SolidColorBrush((Color)v.GetValue(null, null)));

			foreach (var item in colors)
			{
				var menuItem = new MenuItem { Header = item.Key, Background = item.Value };

				menuItem.Click += (_, e) =>
				{
					var source = (MenuItem)e.Source;

					var packetWrapper = (PacketWrapper)outputListBox.Items[outputListBox.SelectedIndex];
					packetWrapper.Color = source.Background;
				};

				outputListBox.ContextMenu.Items.Add(menuItem);
			}
		}

		private void outputListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (outputListBox.SelectedIndex == -1)
			{
				outputTextBox.Text = "";
				return;
			}

			var packetWrapper = (PacketWrapper)outputListBox.Items[outputListBox.SelectedIndex];

			outputTextBox.Text = PacketUtils.PacketToCsCode(packetWrapper.ConanPacket, packetWrapper.Ticks, useTicksInSRCheckBox.IsChecked == true);
			outputHexTextBox.Text = packetWrapper.ConanPacket.Bytes.HexDump();
		}

		private void loadButton_Click(object sender, RoutedEventArgs e)
		{
			outputTextBox.Text = "";
			outputListBox.Items.Clear();

			ReadDump(@"C:\Users\Melvin\Desktop\conan3.pcapng");
		}

		private void saveButton_Click(object sender, RoutedEventArgs e)
		{
			var json = JsonConvert.SerializeObject(outputListBox.Items, Formatting.Indented);
			//var x = JsonConvert.DeserializeObject<PacketWrapper[]>(json);

			File.WriteAllText("c:/temp/conan.json", json);
		}

		private void useTicksInSRCheckBox_Checked(object sender, RoutedEventArgs e)
		{
			//
		}

		private void ReadDump(string path)
		{
			_captureFileReaderDevice?.Dispose();

			_captureFileReaderDevice = new CaptureFileReaderDevice(path);
			_captureFileReaderDevice.OnPacketArrival += OnOnPacketArrival;

			_captureFileReaderDevice.Open();
			_captureFileReaderDevice.StartCapture();
		}

		private byte[] Decompress(Inflater inflater, byte[] input)
		{
			try
			{
				inflater.SetInput(input);

				var bos = new MemoryStream(input.Length);

				var buf = new byte[10240];
				while (!inflater.IsFinished && !inflater.IsNeedingInput)
				{
					var count = inflater.Inflate(buf);
					bos.Write(buf, 0, count);
				}

				return bos.ToArray();
			}
			catch //(Exception e)
			{
				return null;
			}
		}

		private void OnOnPacketArrival(object _, PacketCapture capture)
		{
			var p = capture.GetPacket();
			var ethernetPacket = p.GetPacket();
			var ipPacket = ethernetPacket.PayloadPacket as IPPacket;
			if (ipPacket?.PayloadPacket is not TcpPacket tcpPacket)
				return;

			var sourceAddress = ReverseDnsResolver.GetHostName(ipPacket.SourceAddress) + $":{tcpPacket.SourcePort}";
			var destAddress = ReverseDnsResolver.GetHostName(ipPacket.DestinationAddress) + $":{tcpPacket.DestinationPort}";
			var sourceDestAddress = $"{sourceAddress} > {destAddress}";

			if (!sourceDestAddress.Contains("ageofconan.com"))
				return;

			var serviceName = (sourceAddress.Contains("localhost") ? destAddress : sourceAddress).Replace(".ageofconan.com", "");
			var isDownload = destAddress.Contains("localhost");
			var isFromGameServer = sourceAddress.Contains("gs013-nj4");

			if (!_packetSplitters.ContainsKey(sourceDestAddress))
			{
				_packetSplitters[sourceDestAddress] = new PacketSplitter();
				_packetSplitters[sourceDestAddress].ReceivedPacket += (ticks, packet) =>
				{
					if (outputListBox.Items.Count > 100)
					{
						_captureFileReaderDevice.StopCapture();
						return;
					}

					Dispatcher.Invoke(() => outputListBox.Items.Add(new PacketWrapper(ticks, serviceName, isDownload, packet)));
				};
			}

			var bytes = tcpPacket.PayloadData;
			if (isFromGameServer)
			{
				if (bytes[0] == 0x80 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x05)
				{
					_inflater = new Inflater();
					bytes = Decompress(_inflater, bytes.Skip(9).ToArray());
				}
				else if (_inflater != null)
					bytes = Decompress(_inflater, bytes);
			}

			if (bytes?.Length > 16)
				_packetSplitters[sourceDestAddress].DataReceived(capture.Header.Timeval.Date.Ticks, bytes);
		}
	}
}
