using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TSCutter.GUI.ViewModels;

public partial class JumpTimeViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string? InputText { get; set; }

    [RelayCommand]
    private async Task DelayFocusAsync(RoutedEventArgs e)
    {
        /*
         *  When `DefaultButton = ContentDialogButton.Primary` is set, it is not possible to directly use:
         *  ```xml
         *  <Interaction.Behaviors>
         *      <EventTriggerBehavior EventName="AttachedToVisualTree" SourceObject="{Binding $self}">
         *          <CallMethodAction TargetObject="{Binding $self}" MethodName="Focus"/>
         *      </EventTriggerBehavior>
         *  </Interaction.Behaviors>
         *  ```
         *  to set the focus. Therefore, focus setting is executed with a delay.
         */
        await Task.Delay(100);
        (e.Source as TextBox)?.Focus();
        (e.Source as TextBox)?.SelectAll();
    }
}