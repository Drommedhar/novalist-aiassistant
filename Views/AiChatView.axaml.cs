using Avalonia.Controls;
using Avalonia.Input;
using Novalist.Extensions.AiAssistant.ViewModels;

namespace Novalist.Extensions.AiAssistant.Views;

public partial class AiChatView : UserControl
{
    public AiChatView()
    {
        InitializeComponent();
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter inserts a newline.
        if (e.Key == Key.Enter
            && (e.KeyModifiers & KeyModifiers.Shift) == 0
            && DataContext is AiChatViewModel vm
            && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
