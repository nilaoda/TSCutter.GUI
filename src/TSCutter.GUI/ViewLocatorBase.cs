using System;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;

namespace TSCutter.GUI;

/// <summary>
/// Base implementation of Avalonia ViewLocator. Override GetViewName to customize paths.
/// </summary>
public class ViewLocatorBase : IDataTemplate, IViewLocator, IViewLocatorNavigation
{
    private const string ViewModel = "ViewModel";
    private const string Window = "Window";

    /// <inheritdoc />
    public bool ForceSinglePageNavigation { get; set; }

    /// <summary>
    /// Returns the view type name for specified view model type.
    /// </summary>
    /// <param name="viewModel">The view model to get the view type for.</param>
    /// <returns>The view type name.</returns>
    protected virtual string GetViewName(object viewModel)
    {
        var result = viewModel.GetType().FullName!.Replace(".ViewModels.", ".Views.");
        if (result.EndsWith(ViewModel))
        {
            result = result.Remove(result.Length - ViewModel.Length) + (UseSinglePageNavigation ? "View" : "");
        }
        if (!result.EndsWith(Window))
        {
            result += "View";
        }
        return result;
    }

    /// <inheritdoc />
    public virtual Control Build(object? data)
    {
        try
        {
            return (Control)Create(data!);
        }
        catch (Exception)
        {
            return new TextBlock
            {
                Text = "Not Found: " + GetViewName(data!)
            };
        }
    }

    /// <inheritdoc />
    public virtual ViewDefinition Locate(object viewModel)
    {
        var name = GetViewName(viewModel);
        var viewType = Type.GetType(name, x => Assembly.GetEntryAssembly(), null, false);
        // var viewType = Assembly.GetAssembly(viewModel.GetType())?.GetType(name);

        if (viewType is null || (!typeof(Control).IsAssignableFrom(viewType) && !typeof(Window).IsAssignableFrom(viewType) && !typeof(IView).IsAssignableFrom(viewType)))
        {
            var message = $"Dialog view of type {name} for view model {viewModel.GetType().FullName} is missing.";
            throw new TypeLoadException(message + Environment.NewLine );
        }
        return new ViewDefinition(viewType, () => CreateViewInstance(viewType));
    }

    /// <summary>
    /// The method used to create the view instance from it's <see cref="Type"/>.
    /// Uses <see cref="Activator.CreateInstance(Type)"/> by default.
    /// </summary>
    /// <param name="viewType">The type to create a view for.</param>
    /// <returns>The created view.</returns>
    protected virtual object CreateViewInstance(Type viewType) => Activator.CreateInstance(viewType)!;

    /// <inheritdoc />
    public virtual object Create(object viewModel) =>
        Locate(viewModel).Create();

    /// <inheritdoc />
    public virtual bool Match(object? data) => data is INotifyPropertyChanged;

    /// <inheritdoc />
    public bool UseSinglePageNavigation => Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime || ForceSinglePageNavigation;
}