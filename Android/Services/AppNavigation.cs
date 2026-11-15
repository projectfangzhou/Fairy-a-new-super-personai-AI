using Microsoft.Extensions.DependencyInjection;

namespace FairyAI_Android.Services;

/// <summary>
/// Resolves pages from the app's service provider and pushes them.
///
/// Explicit instead of routing by string: a page that needs services cannot be created by
/// <c>Activator.CreateInstance</c>, and a navigation that silently fails is worse than one that says so.
/// </summary>
public static class AppNavigation
{
    public static TPage? Resolve<TPage>() where TPage : Page =>
        Application.Current?.Handler?.MauiContext?.Services?.GetService<TPage>();

    public static async Task<bool> PushAsync<TPage>(INavigation? navigation) where TPage : Page
    {
        if (navigation is null) return false;

        var page = Resolve<TPage>();
        if (page is null) return false;

        await navigation.PushAsync(page);
        return true;
    }

    public static async Task<bool> GoToTabAsync(string route)
    {
        if (Shell.Current is null) return false;

        await Shell.Current.GoToAsync(route);
        return true;
    }
}
