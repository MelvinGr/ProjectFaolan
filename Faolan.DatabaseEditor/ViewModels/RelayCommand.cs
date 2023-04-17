using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Faolan.DatabaseEditor.ViewModels
{
	public sealed class RelayCommand : ICommand
	{
		private readonly Action _execute;
		private readonly Func<bool>? _canExecute;

		public RelayCommand(Action execute, Func<bool>? canExecute = null)
		{
			_execute = execute;
			_canExecute = canExecute;
		}

		public bool CanExecute(object? parameter)
		{
			return _canExecute?.Invoke() ?? true;
		}

		public void Execute(object? parameter)
		{
			_execute();
		}

		public event EventHandler? CanExecuteChanged;

		public void RaiseCanExecuteChanged()
		{
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	public sealed class AsyncRelayCommand : ICommand
	{
		private readonly Func<bool>? _canExecute;
		private readonly Func<Task> _execute;
		private bool _isExecuting;

		public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
		{
			_execute = execute;
			_canExecute = canExecute;
		}

		public bool CanExecute(object? parameter)
		{
			return !_isExecuting && (_canExecute?.Invoke() ?? true);
		}

		public async void Execute(object? parameter)
		{
			if (!CanExecute(parameter))
				return;

			try
			{
				_isExecuting = true;
				RaiseCanExecuteChanged();

				await _execute();
			}
			finally
			{
				_isExecuting = false;
				RaiseCanExecuteChanged();
			}
		}

		public event EventHandler? CanExecuteChanged;

		public void RaiseCanExecuteChanged()
		{
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
		}
	}
}
