using System.Windows.Input;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>Command abstraction used by the view models (WPF binds to ICommand).</summary>
public interface IAsyncCommand : ICommand
{
    bool IsRunning { get; }

    void RaiseCanExecuteChanged();
}

/// <summary>
/// Async ICommand that swallows nothing: the supplied handler is responsible for
/// converting exceptions to user-visible messages. Re-entrancy is blocked while a
/// run is in flight so double clicks cannot duplicate writes.
/// </summary>
public sealed class AsyncRelayCommand : IAsyncCommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onException;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onException = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onException = onException;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _isRunning;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Last-resort guard. Every handler is expected to catch known failures
            // itself; anything here would otherwise crash the dispatcher.
            if (_onException is not null)
            {
                _onException(ex);
            }
            else
            {
                // async-void commands run on WPF's synchronization context;
                // the application's DispatcherUnhandledException handler reports this.
                throw;
            }
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Synchronous variant for pure in-memory UI actions (tab switches, filters).</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Typed synchronous command; the WPF command parameter is cast to T.</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is T value && (_canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value)
        {
            _execute(value);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
