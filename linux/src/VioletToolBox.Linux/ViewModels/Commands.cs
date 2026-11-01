using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace VioletToolBox.Linux.ViewModels;

/// <summary>Async relay command with automatic disable-while-running.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecute();
        try { await _execute().ConfigureAwait(true); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            throw;
        }
        finally
        {
            _running = false;
            RaiseCanExecute();
        }
    }

    public void RaiseCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Sync relay command.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Relay command with a string parameter (e.g. reboot target, slot name).</summary>
public sealed class ParameterCommand : ICommand
{
    private readonly Action<string?> _execute;

    public event EventHandler? CanExecuteChanged;

    public ParameterCommand(Action<string?> execute) => _execute = execute;

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter as string);
    public void RaiseCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
