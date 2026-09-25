using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages;

/// <summary>"Check prerequisites".</summary>
public partial class PrerequisitesPage : UserControl
{
    private readonly OperationRunner _runner;

    public PrerequisitesPage(OperationRunner runner, IOptions<AppOptions> options)
    {
        _runner = runner;
        InitializeComponent();
        FeatureList.ItemsSource = options.Value.RequiredIisFeatures;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        await _runner.RunAsync("Check prerequisites", async (sp, reporter, ct) =>
        {
            var result = await sp.GetRequiredService<CheckPrerequisitesUseCase>().ExecuteAsync(reporter, ct);
            return result.Success ? result : Result.Fail(result.Error ?? "Some prerequisites are missing.");
        });
    }
}
