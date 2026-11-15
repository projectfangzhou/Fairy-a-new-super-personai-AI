using Microsoft.Extensions.DependencyInjection;
using FairyAI_Android.Pages;

namespace FairyAI_Android;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Every tab page takes constructor dependencies, and a type-based DataTemplate would build
        // them with Activator — which has no parameterless constructor to call. Assigning factories
        // that go through the app's container keeps the pages resolvable and, if one is ever
        // unregistered, says which page instead of failing silently.
        ChatTab.ContentTemplate = Resolve<ChatPage>();
        DashboardTab.ContentTemplate = Resolve<DashboardPage>();
        TasksTab.ContentTemplate = Resolve<TasksPage>();
        SettingsTab.ContentTemplate = Resolve<SettingsPage>();
    }

    private static DataTemplate Resolve<TPage>() where TPage : Page =>
        new(() => Application.Current?.Handler?.MauiContext?.Services?.GetService<TPage>()
                  ?? throw new InvalidOperationException(
                      $"页面 {typeof(TPage).Name} 没有在服务容器里注册"));
}
