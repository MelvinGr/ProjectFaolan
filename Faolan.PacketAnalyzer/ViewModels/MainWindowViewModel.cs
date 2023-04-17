using System.Collections.ObjectModel;

namespace Faolan.PacketAnalyzer.ViewModels
{
	public class MainWindowViewModel : ViewModelBase
	{
		private bool _isBusy;
		private PacketRowViewModel? _selectedPacket;
		private string _agentServerPacketCountText;
		private string _agentServerPortText;
		private string _busyText;
		private string _captureName;
		private string _csPlayerAgentPacketCountText;
		private string _csPlayerAgentPortText;
		private string _downloadPacketsText;
		private string _gameServerPacketCountText;
		private string _gameServerPortText;
		private string _playerAgentPacketCountText;
		private string _playerAgentPortText;
		private string _selectedPacketNotes;
		private string _statusMessage;
		private string _totalBytesText;
		private string _totalPacketsText;
		private string _uniqueOpcodesText;
		private string _uniqueServicesText;
		private string _universeAgentPacketCountText;
		private string _universeAgentPortText;
		private string _unknownPacketsText;
		private string _uploadPacketsText;

		public bool IsBusy
		{
			get => _isBusy;
			set => SetProperty(ref _isBusy, value);
		}

		public ObservableCollection<PacketRowViewModel> PacketRows { get; init; } = [];

		public PacketRowViewModel? SelectedPacket
		{
			get => _selectedPacket;
			set
			{
				if (!SetProperty(ref _selectedPacket, value))
					return;

				SelectedPacketNotes = value?.Notes ?? string.Empty;
			}
		}

		public string AgentServerPacketCountText
		{
			get => _agentServerPacketCountText;
			set => SetProperty(ref _agentServerPacketCountText, value);
		}

		public string AgentServerPortText
		{
			get => _agentServerPortText;
			set => SetProperty(ref _agentServerPortText, value);
		}

		public string BusyText
		{
			get => _busyText;
			set => SetProperty(ref _busyText, value);
		}

		public string CaptureName
		{
			get => _captureName;
			set => SetProperty(ref _captureName, value);
		}

		public string CsPlayerAgentPacketCountText
		{
			get => _csPlayerAgentPacketCountText;
			set => SetProperty(ref _csPlayerAgentPacketCountText, value);
		}

		public string CsPlayerAgentPortText
		{
			get => _csPlayerAgentPortText;
			set => SetProperty(ref _csPlayerAgentPortText, value);
		}

		public string DownloadPacketsText
		{
			get => _downloadPacketsText;
			set => SetProperty(ref _downloadPacketsText, value);
		}

		public string GameServerPacketCountText
		{
			get => _gameServerPacketCountText;
			set => SetProperty(ref _gameServerPacketCountText, value);
		}

		public string GameServerPortText
		{
			get => _gameServerPortText;
			set => SetProperty(ref _gameServerPortText, value);
		}

		public string PlayerAgentPacketCountText
		{
			get => _playerAgentPacketCountText;
			set => SetProperty(ref _playerAgentPacketCountText, value);
		}

		public string PlayerAgentPortText
		{
			get => _playerAgentPortText;
			set => SetProperty(ref _playerAgentPortText, value);
		}

		public string SelectedPacketNotes
		{
			get => _selectedPacketNotes;
			set
			{
				if (!SetProperty(ref _selectedPacketNotes, value))
					return;

				if (SelectedPacket != null)
				{
					SelectedPacket.Notes = string.IsNullOrWhiteSpace(value)
						? null
						: value;
				}
			}
		}

		public string StatusMessage
		{
			get => _statusMessage;
			set => SetProperty(ref _statusMessage, value);
		}

		public string TotalBytesText
		{
			get => _totalBytesText;
			set => SetProperty(ref _totalBytesText, value);
		}

		public string TotalPacketsText
		{
			get => _totalPacketsText;
			set => SetProperty(ref _totalPacketsText, value);
		}

		public string UniqueOpcodesText
		{
			get => _uniqueOpcodesText;
			set => SetProperty(ref _uniqueOpcodesText, value);
		}

		public string UniqueServicesText
		{
			get => _uniqueServicesText;
			set => SetProperty(ref _uniqueServicesText, value);
		}

		public string UniverseAgentPacketCountText
		{
			get => _universeAgentPacketCountText;
			set => SetProperty(ref _universeAgentPacketCountText, value);
		}

		public string UniverseAgentPortText
		{
			get => _universeAgentPortText;
			set => SetProperty(ref _universeAgentPortText, value);
		}

		public string UnknownPacketsText
		{
			get => _unknownPacketsText;
			set => SetProperty(ref _unknownPacketsText, value);
		}

		public string UploadPacketsText
		{
			get => _uploadPacketsText;
			set => SetProperty(ref _uploadPacketsText, value);
		}

		public MainWindowViewModel()
		{
			_busyText = string.Empty;
			_captureName = string.Empty;
			_isBusy = false;
			_selectedPacketNotes = string.Empty;
			_statusMessage = string.Empty;
			_universeAgentPortText = string.Empty;
			_playerAgentPortText = string.Empty;
			_csPlayerAgentPortText = string.Empty;
			_agentServerPortText = string.Empty;
			_gameServerPortText = string.Empty;
			_totalPacketsText = "0";
			_downloadPacketsText = "0";
			_uploadPacketsText = "0";
			_unknownPacketsText = "0";
			_uniqueServicesText = "0";
			_uniqueOpcodesText = "0";
			_totalBytesText = "0 B";
			_universeAgentPacketCountText = "0";
			_playerAgentPacketCountText = "0";
			_csPlayerAgentPacketCountText = "0";
			_agentServerPacketCountText = "0";
			_gameServerPacketCountText = "0";
		}
	}
}
