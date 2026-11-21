using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using FairyAI_Android.Controls;
using FairyAI_Android.Pages;

namespace FairyAI_Android;

public partial class AppShell : Shell
{
    private readonly GlassTabBar _tabBar;

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

        // 自绘的液态玻璃标签栏，浮在内容之上。
        _tabBar = new GlassTabBar();
        _tabBar.SetItems(new[]
        {
            ("对话", "chat", "chat"),
            ("看板", "dashboard", "dashboard"),
            ("任务", "tasks", "tasks"),
            ("设置", "settings", "settings"),
        });

        // Shell 不接受直接往自己身上加子元素，所以覆盖层挂在一个专用的 ShellItem 上，
        // 它不在导航路由里，只用来承载气泡栏。
        OverlayTab.Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection { new(GridLength.Star) },
            Children = { _tabBar },
        };

        Navigated += (_, _) => _tabBar.RefreshSelection();
        CurrentItem = OverlayItem;

        // 起始页是「对话」。Shell 的 GoToAsync 是异步的，这里 fire-and-forget：
        // 首屏要等的就是它，失败的话下面那层已经兜住了（默认 ShellContent）。
        _ = GoToAsync("chat");
    }

    private static DataTemplate Resolve<TPage>() where TPage : Page =>
        new(() => Application.Current?.Handler?.MauiContext?.Services?.GetService<TPage>()
                  ?? throw new InvalidOperationException(
                      $"页面 {typeof(TPage).Name} 没有在服务容器里注册"));
}
