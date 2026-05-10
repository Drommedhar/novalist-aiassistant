using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Novalist.Extensions.AiAssistant.ViewModels;

public partial class KnowledgeScanViewModel : ObservableObject
{
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
}
