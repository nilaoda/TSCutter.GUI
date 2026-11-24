using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Web;

internal static partial class WebUiLauncher
{
    private static bool SameDirectory(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return first == second;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or IOException) { return false; }
    }

    private static async Task<(string Name, string Password)?> AskAccountAsync(Window owner, string[] existingNames)
    {
        var strings = LocalizationManager.Instance;
        var name = new TextBox { Name = "AccountUserName", MaxLength = 32 };
        var password = new TextBox { Name = "AccountPassword", PasswordChar = '●', MaxLength = 128 };
        var confirmation = new TextBox { Name = "AccountPasswordConfirmation", PasswordChar = '●', MaxLength = 128 };
        AutomationProperties.SetName(name, strings.String_WebUi_UserName);
        AutomationProperties.SetName(password, strings.String_WebUi_Password);
        AutomationProperties.SetName(confirmation, strings.String_WebUi_ConfirmPassword);
        // macOS 远程键盘可能不提供有效扫描码，需要保留原生文本输入通道。
        InputMethod.SetIsInputMethodEnabled(password, OperatingSystem.IsMacOS());
        InputMethod.SetIsInputMethodEnabled(confirmation, OperatingSystem.IsMacOS());
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (Application.Current is { } app && app.TryGetResource("AppStatusErrorForegroundBrush", app.ActualThemeVariant, out var resource) && resource is IBrush foreground)
            error.Foreground = foreground;
        var add = new Button { Content = strings.String_WebUi_AddAccount, MinWidth = 90, IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = strings.String_Cancel, IsCancel = true, MinWidth = 80 };
        var dialog = new ClassicWindow
        {
            Title = strings.String_WebUi_AddAccountTitle,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            CanMaximize = false,
            CanMinimize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
            {
                new TextBlock { Text = strings.String_WebUi_AccountsHint, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Spacing = 5, Children = { new TextBlock { Text = strings.String_WebUi_UserName }, name } },
                new StackPanel { Spacing = 5, Children = { new TextBlock { Text = strings.String_WebUi_Password }, password } },
                new StackPanel { Spacing = 5, Children = { new TextBlock { Text = strings.String_WebUi_ConfirmPassword }, confirmation } }, error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { add, cancel } }
            }
            }
        };
        void Validate()
        {
            var duplicate = existingNames.Contains(name.Text?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            var matches = string.Equals(password.Text, confirmation.Text, StringComparison.Ordinal);
            add.IsEnabled = WebUiPassword.ValidUserName(name.Text?.Trim()) && password.Text?.Length is >= 8 and <= 128 && matches && !duplicate;
            error.Text = duplicate ? strings.String_WebUi_DuplicateAccount :
                confirmation.Text is { Length: > 0 } && !matches ? strings.String_WebUi_PasswordMismatch : null;
        }
        name.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty)
                Validate();
        };
        password.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty)
                Validate();
        };
        confirmation.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty)
                Validate();
        };
        dialog.Opened += (_, _) => name.Focus();
        add.Click += (_, _) =>
        {
            Validate();
            if (add.IsEnabled)
                dialog.Close((name.Text!.Trim(), password.Text!));
        };
        cancel.Click += (_, _) => dialog.Close();
        return await dialog.ShowDialog<(string Name, string Password)?>(owner);
    }

    private static Task<bool> ConfirmDirectoryChangeAsync(Window owner, string userName, string inputDirectory, string outputDirectory)
    {
        var strings = LocalizationManager.Instance;
        var save = new Button { Content = strings.String_WebUi_DirectoryRestartConfirm, MinWidth = 120 };
        var cancel = new Button { Content = strings.String_Cancel, IsCancel = true, IsDefault = true, MinWidth = 80 };
        var dialog = new ClassicWindow
        {
            Title = string.Format(strings.String_WebUi_DirectoryRestartTitle, userName),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            CanMaximize = false,
            CanMinimize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
            {
                new TextBlock { Text = strings.String_WebUi_DirectoryRestartMessage, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Spacing = 5, Children =
                {
                    new TextBlock { Text = strings.String_WebUi_UserInputFolder },
                    new TextBox { Text = inputDirectory, IsReadOnly = true },
                    new TextBlock { Text = strings.String_WebUi_UserOutput },
                    new TextBox { Text = outputDirectory, IsReadOnly = true }
                }},
                new TextBlock { Text = strings.String_WebUi_DirectoryRestartNote, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
            }
            }
        };
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return dialog.ShowDialog<bool>(owner);
    }
}
