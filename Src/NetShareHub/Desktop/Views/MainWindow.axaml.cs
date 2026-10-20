using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace NetShareHub.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, OnCopyClick, RoutingStrategies.Bubble);
    }

    // Les boutons « Copier » portent le texte à copier dans leur Tag.
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Tag: string text } && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
