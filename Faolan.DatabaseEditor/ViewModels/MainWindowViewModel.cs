using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.DatabaseEditor.ViewModels
{
	public sealed class MainWindowViewModel : INotifyPropertyChanged
	{
		// The editor is a single-user desktop tool: one editing session = one unit of work, so it holds a single
		// context (from the shared pooled factory) for the window's lifetime - load into it, edit tracked entities
		// via the UI, save on command. (The server, by contrast, uses short-lived per-operation contexts.)
		private readonly DatabaseContext? _context;

		public Account? SelectedAccount
		{
			get;
			set
			{
				if (ReferenceEquals(field, value))
					return;

				field = value;

				RefreshSelectedAccount();
				OnPropertyChanged(nameof(SelectedAccount));
			}
		}

		public AsyncRelayCommand SaveCommand { get; }

		public Character? SelectedCharacter
		{
			get;
			set
			{
				if (ReferenceEquals(field, value))
					return;

				field = value;

				RefreshSelectedCharacter();
				OnPropertyChanged(nameof(SelectedCharacter));
			}
		}

		public ObservableCollection<Account> Accounts { get; } = [];

		public ObservableCollection<Character> Characters { get; } = [];

		public ObservableCollection<PropertyEditorViewModel> SelectedAccountEditors { get; } = [];

		public ObservableCollection<PropertyEditorViewModel> SelectedCharacterEditors { get; } = [];

		// Design-time constructor
		public MainWindowViewModel()
		{
			SaveCommand = new AsyncRelayCommand(Save);

			var account = new Account
			{
				// Fill these only if your Account type allows it.
				// Name = "Design account"
			};

			var character = new Character
			{
				// Name = "Design character"
			};

			Accounts.Add(account);
			Characters.Add(character);

			SelectedAccount = account;
			SelectedCharacter = character;
		}

		// Runtime constructor
		public MainWindowViewModel(IDbContextFactory<DatabaseContext> contextFactory)
		{
			SaveCommand = new AsyncRelayCommand(Save);

			_context = contextFactory.CreateDbContext();

			var accounts = _context.Accounts.Include(a => a.Characters).ToList();

			foreach (var account in accounts)
				Accounts.Add(account);

			SelectedAccount = Accounts.FirstOrDefault();
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged(string propertyName)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		private void RefreshSelectedAccount()
		{
			SelectedAccountEditors.Clear();
			Characters.Clear();
			SelectedCharacterEditors.Clear();

			if (SelectedAccount is null)
			{
				SelectedCharacter = null;
				return;
			}

			AddEditors(SelectedAccountEditors, SelectedAccount);

			if (SelectedAccount.Characters is not null)
			{
				foreach (var character in SelectedAccount.Characters)
					Characters.Add(character);
			}

			SelectedCharacter = Characters.FirstOrDefault();
		}

		private void RefreshSelectedCharacter()
		{
			SelectedCharacterEditors.Clear();

			if (SelectedCharacter is null)
				return;

			AddEditors(SelectedCharacterEditors, SelectedCharacter);
		}

		private Task Save()
		{
			return _context?.SaveChangesAsync() ?? Task.CompletedTask;
		}

		private static void AddEditors(ObservableCollection<PropertyEditorViewModel> editors, object model)
		{
			var properties = model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead && p.CanWrite).Where(p => p.GetIndexParameters().Length == 0).Where(p => IsEditableType(p.PropertyType))
				.OrderBy(p => p.Name);

			foreach (var property in properties)
				editors.Add(new PropertyEditorViewModel(model, property));
		}

		private static bool IsEditableType(Type type)
		{
			type = Nullable.GetUnderlyingType(type) ?? type;

			return type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
			       type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid) || type.IsEnum;
		}
	}
}
