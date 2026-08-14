namespace CodeAnalyzer.App.Services;

/// <summary>
/// Marshals work onto the UI thread. Exists so view models and Core services can
/// post updates without referencing WPF's Dispatcher directly, which keeps them testable.
/// </summary>
public interface IUiDispatcher
{
    bool IsOnUiThread { get; }

    void Post(Action action);

    Task InvokeAsync(Action action);
}
