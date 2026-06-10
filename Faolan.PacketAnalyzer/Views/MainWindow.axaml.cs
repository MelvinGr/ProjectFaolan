using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Faolan.Core.Extensions;
using Faolan.PacketAnalyzer.ViewModels;

namespace Faolan.PacketAnalyzer.Views
{
	public partial class MainWindow : Window
	{
		private const string DefaultKey = "--";
		private readonly List<PacketRowViewModel> _allPacketRows = [];
		private readonly PacketAnalysisService _analysisService = new PacketAnalysisService();
		private readonly PacketDumpFileService _dumpFileService = new PacketDumpFileService();
		private readonly PacketSummaryService _summaryService = new PacketSummaryService();
		private PacketAnalyzerPortSettings _portSettings = PacketAnalyzerPortSettings.Empty;

		private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;
		private ObservableCollection<PacketRowViewModel> PacketRows => ViewModel.PacketRows;

		private string SourceComboBoxSelectedItem
		{
			get => sourceComboBox.SelectedItem as string ?? DefaultKey;
			set => sourceComboBox.SelectedItem = value;
		}

		public MainWindow()
		{
			InitializeComponent();
			searchTextTextBox.PropertyChanged += (_, e) =>
			{
				if (e.Property == TextBox.TextProperty)
					ApplyFilters();
			};

			useTicksInSRCheckBox.PropertyChanged += (_, e) =>
			{
				if (e.Property == ToggleButton.IsCheckedProperty)
					RefreshSelectedPacketDetails();
			};

			var exportMenuItem = new MenuItem { Header = "Export Selected to Clipboard" };
			exportMenuItem.Click += ExportMenuItemOnClick;

			var contextMenu = new ContextMenu();
			contextMenu.Items.Add(exportMenuItem);

			var colorsMenuItem = new MenuItem { Header = "Colors" };
			contextMenu.Items.Add(colorsMenuItem);

			var colors = typeof(Colors)
				.GetProperties(BindingFlags.Static | BindingFlags.Public)
				.Where(p => p.Name.StartsWith("Light", StringComparison.Ordinal))
				.Select(p => new
				{
					p.Name,
					Value = p.GetValue(null, null)
				})
				.Where(p => p.Value is Color)
				.ToDictionary(p => p.Name, p => new SolidColorBrush((Color)p.Value!));

			foreach (var item in colors)
			{
				var menuItem = new MenuItem
				{
					Header = item.Key,
					Background = item.Value
				};

				menuItem.Click += (_, e) =>
				{
					if (packetDataGrid.SelectedItem is not PacketRowViewModel packetRow)
						return;

					if (e.Source is MenuItem source && source.Background != null)
						packetRow.Color = source.Background;
				};

				colorsMenuItem.Items.Add(menuItem);
			}

			packetDataGrid.ContextMenu = contextMenu;

			packetDataGrid.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;
			packetDataGrid.CopyingRowClipboardContent += (_, e) =>
			{
				var packetRow = e.Item as PacketRowViewModel;
				if (packetRow == null)
					return;

				e.ClipboardRowContent.Clear();

				var content = packetRow.ConanPacket.PacketToCsCode(packetRow.Ticks, useTicksInSRCheckBox.IsChecked == true);
				e.ClipboardRowContent.Add(new DataGridClipboardCellContent(packetRow, new DataGridTextColumn(), content));
			};
		}

		private void ApplyFilters()
		{
			var selectedPacketTicks = ViewModel.SelectedPacket?.Ticks;
			var filteredPackets = _allPacketRows
				.Where(MatchesCurrentFilters)
				.ToArray();

			PacketRows.Clear();
			foreach (var packet in filteredPackets)
				PacketRows.Add(packet);

			RefreshSummary();

			if (!string.IsNullOrWhiteSpace(selectedPacketTicks))
			{
				var selectedPacket = PacketRows.FirstOrDefault(packet => string.Equals(packet.Ticks, selectedPacketTicks, StringComparison.Ordinal));
				packetDataGrid.SelectedItem = selectedPacket;
				ViewModel.SelectedPacket = selectedPacket;
			}

			if (PacketRows.Count == 0)
				ClearSelectedPacketDetails();
		}

		private void ApplyPortSettings(PacketAnalyzerPortSettings portSettings)
		{
			ViewModel.UniverseAgentPortText = FormatPort(portSettings.UniverseAgentPort);
			ViewModel.PlayerAgentPortText = FormatPort(portSettings.PlayerAgentPort);
			ViewModel.CsPlayerAgentPortText = FormatPort(portSettings.CsPlayerAgentPort);
			ViewModel.AgentServerPortText = FormatPort(portSettings.AgentServerPort);
			ViewModel.GameServerPortText = FormatPort(portSettings.GameServerPort);
		}

		private void ClearSelectedPacketDetails()
		{
			packetDataGrid.SelectedItem = null;
			outputTextBox.Text = string.Empty;
			outputHexTextBox.Text = string.Empty;
			ViewModel.SelectedPacket = null;
		}

		private void ClearVisibleState()
		{
			PacketRows.Clear();
			ClearSelectedPacketDetails();
		}

		private async void ExportMenuItemOnClick(object? sender, RoutedEventArgs e)
		{
			var selectedItems = packetDataGrid
				.SelectedItems
				.OfType<PacketRowViewModel>()
				.ToArray();
			if (selectedItems.Length == 0)
			{
				ViewModel.StatusMessage = "Select one or more packets to export.";
				return;
			}

			var output = selectedItems
				.Select((i, ii) => i.ConanPacket.PacketToCsCode(i.Ticks + "_" + ii, useTicksInSRCheckBox.IsChecked == true))
				.JoinAsString("\r\n");
			var clipboard = GetTopLevel(this)
				?.Clipboard;
			if (clipboard == null)
			{
				ViewModel.StatusMessage = "Clipboard is not available in the current environment.";
				return;
			}

			try
			{
				await clipboard.SetTextAsync(output);
				ViewModel.StatusMessage = $"Copied {selectedItems.Length:N0} packet snippet(s) to the clipboard.";
			}
			catch (Exception exception)
			{
				ViewModel.StatusMessage = $"Failed to copy packet output: {exception.Message}";
			}
		}

		private async void importButton_Click(object? sender, RoutedEventArgs e)
		{
			if (ViewModel.IsBusy)
				return;

			var topLevel = GetTopLevel(this);
			if (topLevel is null)
				return;

			var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
			{
				AllowMultiple = false,
				FileTypeFilter =
				[
					new FilePickerFileType("Cap files") { Patterns = ["*.pcapng"] },
					new FilePickerFileType("All files") { Patterns = ["*"] }
				]
			});

			if (result.Count == 0)
				return;

			var path = result[0].Path.LocalPath;
			if (string.IsNullOrWhiteSpace(path))
				return;

			var captureName = Path.GetFileName(path);
			SetBusyState(true, $"Importing {captureName}...");

			try
			{
				var importResult = await Task.Run(() => _analysisService.AnalyzeCapture(path));

				_portSettings = importResult.PortSettings;
				ApplyPortSettings(_portSettings);
				ViewModel.CaptureName = captureName;

				ClearVisibleState();
				_allPacketRows.Clear();
				outputTextBox.Text = string.Empty;
				outputHexTextBox.Text = string.Empty;

				foreach (var packet in importResult.Packets)
					_allPacketRows.Add(new PacketRowViewModel(packet));

				ApplyFilters();
				RefreshSummary();
				UpdateSourceComboBox();
				ViewModel.StatusMessage = CreateImportStatusMessage(captureName, importResult.PortSettings, importResult.Packets.Count);
			}
			catch (Exception exception)
			{
				ViewModel.StatusMessage = $"Failed to import {captureName}: {exception.Message}";
			}
			finally
			{
				SetBusyState(false);
			}
		}

		private async void loadButton_Click(object? sender, RoutedEventArgs e)
		{
			var topLevel = GetTopLevel(this);
			if (topLevel is null)
				return;

			var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
			{
				AllowMultiple = false,
				Title = "Load packet dump JSON",
				FileTypeFilter =
				[
					new FilePickerFileType("JSON files") { Patterns = ["*.json"] },
					new FilePickerFileType("All files") { Patterns = ["*"] }
				]
			});

			if (result.Count == 0)
				return;

			var path = result[0].Path.LocalPath;
			if (string.IsNullOrWhiteSpace(path))
				return;

			outputTextBox.Text = "";
			outputHexTextBox.Text = "";
			ClearVisibleState();

			var loadResult = await Task.Run(() => _dumpFileService.Load(path));
			_portSettings = loadResult.PortSettings;
			ApplyPortSettings(_portSettings);

			_allPacketRows.Clear();
			foreach (var item in loadResult.PacketRows)
				_allPacketRows.Add(item);

			ApplyFilters();
			ViewModel.CaptureName = loadResult.CaptureName;
			ViewModel.StatusMessage = $"Loaded {PacketRows.Count:N0} packets from {Path.GetFileName(path)} ({loadResult.SchemaVersion})";
			RefreshSummary();

			UpdateSourceComboBox();
		}

		private bool MatchesCurrentFilters(PacketRowViewModel packet)
		{
			var selectedService = SourceComboBoxSelectedItem;
			if (!string.Equals(selectedService, DefaultKey, StringComparison.Ordinal) && !string.Equals(packet.ServiceName, selectedService, StringComparison.OrdinalIgnoreCase))
				return false;

			var searchText = searchTextTextBox.Text?.Trim();
			if (string.IsNullOrWhiteSpace(searchText))
				return true;

			if (searchText.Length <= 2)
				return true;

			return Contains(packet.Ticks, searchText) || Contains(packet.ServiceName, searchText) || Contains(packet.ProtocolName, searchText) || Contains(packet.Opcode, searchText) ||
			       Contains(packet.GameServerOpcode, searchText) || Contains(packet.SourceAddress, searchText) || Contains(packet.DestinationAddress, searchText) || Contains(packet.BytesStringDisplay, searchText) ||
			       Contains(packet.BytesAscii, searchText) || Contains(packet.Notes, searchText);
		}

		private void packetDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if (packetDataGrid.SelectedIndex == -1)
			{
				outputTextBox.Text = "";
				outputHexTextBox.Text = "";
				ViewModel.SelectedPacket = null;
				return;
			}

			var packetRow = (PacketRowViewModel)packetDataGrid.SelectedItem;
			ViewModel.SelectedPacket = packetRow;
			RefreshSelectedPacketDetails();
		}

		private void RefreshSelectedPacketDetails()
		{
			if (packetDataGrid.SelectedItem is not PacketRowViewModel packetRow)
			{
				outputTextBox.Text = string.Empty;
				outputHexTextBox.Text = string.Empty;
				return;
			}

			outputTextBox.Text = packetRow.ConanPacket?.PacketToCsCode(packetRow.Ticks, useTicksInSRCheckBox.IsChecked == true);
			outputHexTextBox.Text = packetRow.Bytes.HexDump();
		}

		private void RefreshSummary()
		{
			var summary = _summaryService.Compute(PacketRows.Select(packetRow => packetRow.Packet));

			ViewModel.TotalPacketsText = FormatCount(summary.TotalPackets);
			ViewModel.DownloadPacketsText = FormatCount(summary.DownloadPackets);
			ViewModel.UploadPacketsText = FormatCount(summary.UploadPackets);
			ViewModel.UnknownPacketsText = FormatCount(summary.UnknownPackets);
			ViewModel.UniqueServicesText = FormatCount(summary.UniqueServices);
			ViewModel.UniqueOpcodesText = FormatCount(summary.UniqueOpcodes);
			ViewModel.TotalBytesText = FormatBytes(summary.TotalBytes);
			ViewModel.UniverseAgentPacketCountText = FormatCount(summary.UniverseAgentPackets);
			ViewModel.PlayerAgentPacketCountText = FormatCount(summary.PlayerAgentPackets);
			ViewModel.CsPlayerAgentPacketCountText = FormatCount(summary.CsPlayerAgentPackets);
			ViewModel.AgentServerPacketCountText = FormatCount(summary.AgentServerPackets);
			ViewModel.GameServerPacketCountText = FormatCount(summary.GameServerPackets);
		}

		private async void saveButton_Click(object? sender, RoutedEventArgs e)
		{
			var topLevel = GetTopLevel(this);
			if (topLevel is null)
				return;

			var captureName = !string.IsNullOrWhiteSpace(ViewModel.CaptureName)
				? Path.GetFileNameWithoutExtension(ViewModel.CaptureName)
				: "packet-dump";

			var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
			{
				Title = "Save packet dump JSON",
				SuggestedFileName = $"{captureName}.json",
				DefaultExtension = "json",
				ShowOverwritePrompt = true,
				FileTypeChoices =
				[
					new FilePickerFileType("JSON files") { Patterns = ["*.json"] },
					new FilePickerFileType("All files") { Patterns = ["*"] }
				]
			});

			if (result is null)
				return;

			var jsonPath = result.Path.LocalPath;
			if (string.IsNullOrWhiteSpace(jsonPath))
				return;

			_dumpFileService.Save(jsonPath, captureName, _portSettings, PacketRows);

			var packetRow = packetDataGrid.SelectedItem as PacketRowViewModel;
			if (packetRow != null)
			{
				_dumpFileService.SaveSelectedPacketBytes(jsonPath, packetRow.Packet);
				var selectedPacketPath = Path.Combine(Path.GetDirectoryName(jsonPath) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(jsonPath)}.selected.bin");
				ViewModel.StatusMessage = $"Saved JSON to {Path.GetFileName(jsonPath)} and selected packet bytes to {Path.GetFileName(selectedPacketPath)}";
				return;
			}

			ViewModel.StatusMessage = $"Saved JSON to {Path.GetFileName(jsonPath)}";
		}

		private void SetBusyState(bool isBusy, string? busyText = null)
		{
			ViewModel.IsBusy = isBusy;
			ViewModel.BusyText = isBusy
				? busyText ?? "Working..."
				: string.Empty;

			importButton.IsEnabled = !isBusy;
			loadButton.IsEnabled = !isBusy;
			saveButton.IsEnabled = !isBusy;
			packetDataGrid.IsEnabled = !isBusy;
			sourceComboBox.IsEnabled = !isBusy;
			searchTextTextBox.IsEnabled = !isBusy;
			useTicksInSRCheckBox.IsEnabled = !isBusy;
		}

		private void sourceComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			ApplyFilters();
		}

		private void UpdateSourceComboBox()
		{
			sourceComboBox.Items.Clear();
			sourceComboBox.Items.Add(DefaultKey);

			foreach (var v in _allPacketRows
				         .Select(p => p.ServiceName)
				         .Distinct()
				         .OrderBy(p => p))
				sourceComboBox.Items.Add(v);

			SourceComboBoxSelectedItem = DefaultKey;
		}

		private static void AddDetectedPort(ICollection<string> detectedPorts, string label, bool wasDetected, ushort port)
		{
			if (wasDetected && port > 0)
				detectedPorts.Add($"{label}={port}");
		}

		private static bool Contains(string? value, string searchText)
		{
			return !string.IsNullOrWhiteSpace(value) && value.Contains(searchText, StringComparison.OrdinalIgnoreCase);
		}

		private static string CreateImportStatusMessage(string captureName, PacketAnalyzerPortSettings detectionResult, int packetCount)
		{
			var detectedPorts = new List<string>();

			AddDetectedPort(detectedPorts, "Universe", detectionResult.UniverseAgentPort > 0, detectionResult.UniverseAgentPort);
			AddDetectedPort(detectedPorts, "Player", detectionResult.PlayerAgentPort > 0, detectionResult.PlayerAgentPort);
			AddDetectedPort(detectedPorts, "CS Player", detectionResult.CsPlayerAgentPort > 0, detectionResult.CsPlayerAgentPort);
			AddDetectedPort(detectedPorts, "Agent", detectionResult.AgentServerPort > 0, detectionResult.AgentServerPort);
			AddDetectedPort(detectedPorts, "Game", detectionResult.GameServerPort > 0, detectionResult.GameServerPort);

			var detectedPortSummary = detectedPorts.Count > 0
				? $" Detected ports: {string.Join(", ", detectedPorts)}."
				: " No service ports were auto-detected from the capture.";

			return $"Imported {packetCount:N0} packets from {captureName}.{detectedPortSummary}";
		}

		private static string FormatBytes(long byteCount)
		{
			string[] suffixes = ["B", "KB", "MB", "GB"];
			double value = byteCount;
			var suffixIndex = 0;

			while (value >= 1024 && suffixIndex < suffixes.Length - 1)
			{
				value /= 1024;
				suffixIndex++;
			}

			return suffixIndex == 0
				? $"{byteCount:N0} {suffixes[suffixIndex]}"
				: $"{value:N1} {suffixes[suffixIndex]}";
		}

		private static string FormatCount(int count)
		{
			return count.ToString("N0");
		}

		//////////////////

		private static string FormatPort(ushort port)
		{
			return port > 0
				? port.ToString()
				: string.Empty;
		}
	}
}
