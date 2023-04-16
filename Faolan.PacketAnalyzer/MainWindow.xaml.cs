using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        private const string _defaultKey = "--";

        private readonly Dictionary<string, PacketSplitter> _packetSplitters = new();

        private ICaptureDevice _captureFileReaderDevice;
        private Inflater _inflater;

        private PacketWrapperCollection PacketWrappers => (PacketWrapperCollection)Resources["packetWrappers"];

        private string SourceComboBoxSelectedItem
        {
            get => (string)sourceComboBox.SelectedItem;
            set => sourceComboBox.SelectedItem = value;
        }

        public MainWindow()
        {
            InitializeComponent();
            
            packetDataGrid.ContextMenu = new ContextMenu();

            var exportMenuItem = new MenuItem { Header = "Export Selected to Clipboard" };
            exportMenuItem.Click += ExportMenuItemOnClick;

            packetDataGrid.ContextMenu.Items.Add(exportMenuItem);

            var colorsMenuItem = new MenuItem { Header = "Colors" };
            packetDataGrid.ContextMenu.Items.Add(colorsMenuItem);

            var colors = typeof(Colors)
                .GetProperties(BindingFlags.Static | BindingFlags.Public)
                .Where(p => p.Name.StartsWith("Light"))
                .ToDictionary(p => p.Name, v => new SolidColorBrush((Color)v.GetValue(null, null)));

            foreach (var item in colors)
            {
                var menuItem = new MenuItem
                {
                    Header = item.Key,
                    Background = item.Value
                };

                menuItem.Click += (_, e) =>
                {
                    var source = (MenuItem)e.Source;

                    var packetWrapper = (PacketWrapper)packetDataGrid.Items[packetDataGrid.SelectedIndex];
                    packetWrapper.Color = source.Background;
                };

                colorsMenuItem.Items.Add(menuItem);
            }
        }

        private void ExportMenuItemOnClick(object sender, RoutedEventArgs e)
        {
            var selectedItems = packetDataGrid.SelectedItems.OfType<PacketWrapper>().ToArray();

            var x = selectedItems.Select((i, ii) => PacketUtils.PacketToCsCode(i.ConanPacket as ConanPacket, i.Ticks + "_" +ii, useTicksInSRCheckBox.IsChecked == true)).JoinAsString("\r\n");

            try
            {
                Clipboard.SetText(x);
            }
            catch
            {
                //
            }
        }

        private void packetDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (packetDataGrid.SelectedIndex == -1)
            {
                outputTextBox.Text = "";
                return;
            }

            var packetWrapper = (PacketWrapper)packetDataGrid.Items[packetDataGrid.SelectedIndex];

            notesTextBox.DataContext = packetWrapper;
            outputTextBox.Text = PacketUtils.PacketToCsCode(packetWrapper.ConanPacket as ConanPacket, packetWrapper.Ticks, useTicksInSRCheckBox.IsChecked == true);
            outputHexTextBox.Text = packetWrapper.ConanPacket.Bytes.HexDump();
        }

        private void importButton_Click(object sender, RoutedEventArgs e)
        {
            outputTextBox.Text = "";
            PacketWrappers.Clear();

            ReadDump(@"C:\Users\Melvin\Desktop\conann.pcapng");
            UpdateSourceComboBox();
        }

        private void loadButton_Click(object sender, RoutedEventArgs e)
        {
            outputTextBox.Text = "";
            PacketWrappers.Clear();

            var json = File.ReadAllText("c:/temp/conan.json");
            var items = JsonConvert.DeserializeObject<PacketWrapper[]>(json);

            foreach (var item in items)
                PacketWrappers.Add(item);

            UpdateSourceComboBox();
        }

        private void saveButton_Click(object sender, RoutedEventArgs e)
        {
            var json = JsonConvert.SerializeObject(PacketWrappers, Formatting.Indented);
            //var x = JsonConvert.DeserializeObject<PacketWrapper[]>(json);

            File.WriteAllText("c:/temp/conan.json", json);
        }

        private void useTicksInSRCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            //
        }

        private void packetWrappers_Filter(object sender, FilterEventArgs e)
        {
            var packetWrapper = (PacketWrapper)e.Item;

            e.Accepted = (SourceComboBoxSelectedItem is null or _defaultKey || packetWrapper.ServiceName == SourceComboBoxSelectedItem) &&
                         (string.IsNullOrEmpty(searchTextTextBox.Text) || packetWrapper.BytesStringDisplay.ContainsIgnoreCase(searchTextTextBox.Text));
        }

        private void sourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var listCollectionView = (ListCollectionView)packetDataGrid.ItemsSource;
            listCollectionView.Refresh();
        }

        private void UpdateSourceComboBox()
        {
            sourceComboBox.ItemsSource = new [] { _defaultKey }.AppendRange(PacketWrappers.Select(p => p.ServiceName).Distinct().OrderBy(p => p));//.ToArray();
            SourceComboBoxSelectedItem = _defaultKey;
        }

        private void searchTextTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (searchTextTextBox.Text.Length != 0 && searchTextTextBox.Text.Length <= 2)
                return;

            var listCollectionView = (ListCollectionView)packetDataGrid.ItemsSource;
            listCollectionView.Refresh();
        }

        //////////////////

        private void ReadDump(string path)
        {
            _captureFileReaderDevice?.Dispose();

            _captureFileReaderDevice = new CaptureFileReaderDevice(path); // CaptureDeviceList.Instance.SingleOrDefault(i => i.Description == "Intel(R) Wi-Fi 6 AX201 160MHz");
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

        private readonly int[] ports = Enumerable.Range(7000, 100).ToArray();


        private void OnOnPacketArrival(object _, PacketCapture capture)
        {
            var p = capture.GetPacket();
            var ethernetPacket = p.GetPacket();
            var ipPacket = ethernetPacket.PayloadPacket as IPPacket;
            if (ipPacket?.PayloadPacket is not TcpPacket tcpPacket)
                return;

            if (!ports.Contains(tcpPacket.SourcePort) && !ports.Contains(tcpPacket.DestinationPort))
                return;

            var sourceAddress = ReverseDnsResolver.GetHostName(ipPacket.SourceAddress) + $":{tcpPacket.SourcePort}";
            var destAddress = ReverseDnsResolver.GetHostName(ipPacket.DestinationAddress) + $":{tcpPacket.DestinationPort}";
            var sourceDestAddress = $"{sourceAddress} > {destAddress}";

            if (!sourceDestAddress.Contains("ageofconan.com"))
                return;
            
            var serviceName = (sourceAddress.Contains("localhost") ? destAddress : sourceAddress).Replace(".ageofconan.com", "");
            var isDownload = destAddress.Contains("localhost");
            var isFromGameServer = sourceAddress.StartsWith("gs0");
            var isAgentServer = tcpPacket.DestinationPort == 7002 || tcpPacket.SourcePort == 7002;

            if (isAgentServer)
            {
                Console.WriteLine("Got AgentServer packet!");
                return;
            }

            if (!_packetSplitters.ContainsKey(sourceDestAddress))
            {
                _packetSplitters[sourceDestAddress] = new PacketSplitter(sourceDestAddress);
                _packetSplitters[sourceDestAddress].ReceivedPacket += (ticks, packet) =>
                {
                    
                        Dispatcher.Invoke(() => PacketWrappers.Add(new PacketWrapper(ticks, serviceName, isDownload, packet)));
                };
            }

            var bytes = tcpPacket.PayloadData;
            if (isFromGameServer && bytes?.Length > 0)
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

    public class PacketWrapperCollection : ObservableCollection<PacketWrapper>
    {
        // Creating the Tasks collection in this way enables data binding from XAML.
    }
}
