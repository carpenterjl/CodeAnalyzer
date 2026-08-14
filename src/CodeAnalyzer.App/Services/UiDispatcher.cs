using System.Windows;
using System.Windows.Threading;

namespace CodeAnalyzer.App.Services;

public sealed class UiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    public bool IsOnUiThread => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (IsOnUiThread)
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    public Task InvokeAsync(Action action)
    {
        if (IsOnUiThread)
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }
}
