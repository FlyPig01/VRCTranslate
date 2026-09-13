using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VrcTranslate.Desktop.Pages;

public sealed partial class RunPage : Page
{
    private AppState State => ((App)Microsoft.UI.Xaml.Application.Current).State;

    public RunPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshRoute();
        Loaded += (_, _) => QueueResponsiveLayout();
        FeatureGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
        RouteSummaryGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContentColumn is not null)
            ContentColumn.Width = Math.Max(0, Math.Min(1120, e.NewSize.Width - 64));
        QueueResponsiveLayout();
    }

    private void QueueResponsiveLayout()
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            LayoutSummary(RouteSummaryGrid?.ActualWidth ?? 0);
            LayoutFeatures(FeatureGrid?.ActualWidth ?? 0);
        });
    }

    private void LayoutSummary(double width)
    {
        if (RouteSummaryGrid is null || width <= 0) return;

        // The shell can be resized to a narrow game-monitor window. Keep the
        // route and model readable by moving the action below them instead of
        // allowing the three desktop columns to collapse into slivers.
        var columns = width >= 760 ? 3 : width >= 500 ? 2 : 1;
        RouteSummaryGrid.ColumnDefinitions.Clear();
        RouteSummaryGrid.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            RouteSummaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = index == columns - 1 && columns == 3 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });

        var rows = columns == 3 ? 1 : columns == 2 ? 2 : 3;
        for (var index = 0; index < rows; index++)
            RouteSummaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumn(RouteSummaryService, 0);
        Grid.SetRow(RouteSummaryService, 0);
        Grid.SetColumn(RouteSummaryModel, columns == 3 ? 1 : columns == 2 ? 1 : 0);
        Grid.SetRow(RouteSummaryModel, columns == 3 ? 0 : columns == 2 ? 0 : 1);
        Grid.SetColumn(RouteSummaryButton, columns == 3 ? 2 : 0);
        Grid.SetRow(RouteSummaryButton, columns == 3 ? 0 : columns == 2 ? 1 : 2);
        Grid.SetColumnSpan(RouteSummaryButton, columns == 2 ? 2 : 1);
        RouteSummaryButton.HorizontalAlignment = HorizontalAlignment.Left;
        RouteSummaryButton.VerticalAlignment = VerticalAlignment.Bottom;
    }

    private void LayoutFeatures(double width)
    {
        if (FeatureGrid is null) return;
        if (width <= 0)
            width = ContentColumn?.ActualWidth ?? 0;
        if (width <= 0) return;
        var columns = width >= 860 ? 3 : width >= 560 ? 2 : 1;
        FeatureGrid.ColumnDefinitions.Clear();
        FeatureGrid.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            FeatureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var childCount = FeatureGrid.Children.Count;
        var rows = (int)Math.Ceiling(childCount / (double)columns);
        for (var index = 0; index < rows; index++)
            FeatureGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < childCount; index++)
        {
            if (FeatureGrid.Children[index] is FrameworkElement child)
            {
                Grid.SetColumn(child, index % columns);
                Grid.SetRow(child, index / columns);
            }
        }
    }

    private void RefreshRoute()
    {
        var route = State.CurrentRoute;
        RouteName.Text = route.DisplayName;
        ModelSummary.Text = $"{route.Profile.Provider} / {route.Profile.Model}";
    }

    private void OnOpenTranslationClicked(object sender, RoutedEventArgs e)
    {
        if (Frame is not null) Frame.Navigate(typeof(TranslationPage));
    }

    private void OnOpenInputClicked(object sender, RoutedEventArgs e)
    {
        if (Frame is not null) Frame.Navigate(typeof(SelfMessagePage));
    }

    private void OnOpenVoiceClicked(object sender, RoutedEventArgs e)
    {
        if (Frame is not null) Frame.Navigate(typeof(VoicePage));
    }

    private void OnOpenSelfVoiceClicked(object sender, RoutedEventArgs e)
    {
        if (Frame is not null) Frame.Navigate(typeof(SelfMessagePage));
    }
}
