using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.ViewModels;

public partial class KnowledgeScanViewModel : ObservableObject, IDisposable
{
    public IExtensionLocalization? Loc { get; set; }

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isComplete;

    private readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;

    public TaskCompletionSource Closed { get; } = new();

    public void Report(double fraction, string status)
    {
        Progress = fraction;
        Status = status;
    }

    public void MarkComplete()
    {
        Progress = 1;
        IsRunning = false;
        IsComplete = true;
    }

    [RelayCommand]
    public void Cancel()
    {
        _cts.Cancel();
        Closed.TrySetResult();
    }

    [RelayCommand]
    public void Close()
    {
        Closed.TrySetResult();
    }

    public void Start()
    {
        IsRunning = true;
        IsComplete = false;
        Progress = 0;
        Status = string.Empty;
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
