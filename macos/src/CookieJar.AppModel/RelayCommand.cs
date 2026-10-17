using System;
using System.Windows.Input;

namespace CookieJar.AppModel
{
    /// <summary>Minimal ICommand over an action and an optional can-execute test.</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute();
        }

        public void Execute(object parameter)
        {
            if (CanExecute(parameter)) _execute();
        }

        public void RaiseCanExecuteChanged()
        {
            EventHandler h = CanExecuteChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
