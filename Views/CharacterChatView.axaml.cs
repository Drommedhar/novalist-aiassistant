using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Novalist.Extensions.AiAssistant.ViewModels;

namespace Novalist.Extensions.AiAssistant.Views;

public partial class CharacterChatView : UserControl
{
    private CharacterChatViewModel? _vm;

    public CharacterChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm != null)
        {
            _vm.Turns.CollectionChanged -= OnTurnsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as CharacterChatViewModel;
        if (_vm != null)
        {
            _vm.Turns.CollectionChanged += OnTurnsChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToEndDeferred();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CharacterChatViewModel.StreamingResponse))
            ScrollToEndDeferred();
    }

    private bool _pendingScrollToEnd;

    private void ScrollToEndDeferred()
    {
        // Wait for the next layout pass so the new content is measured before
        // we scroll. Avalonia's ScrollViewer.Extent doesn't update until after
        // measure/arrange, so an immediate ScrollToEnd lands one frame short.
        if (_pendingScrollToEnd) return;
        _pendingScrollToEnd = true;
        if (ChatScroll != null)
            ChatScroll.LayoutUpdated += OnChatScrollLayoutUpdated;
    }

    private void OnChatScrollLayoutUpdated(object? sender, System.EventArgs e)
    {
        if (ChatScroll == null) return;
        ChatScroll.LayoutUpdated -= OnChatScrollLayoutUpdated;
        _pendingScrollToEnd = false;
        ChatScroll.ScrollToEnd();
    }
}
