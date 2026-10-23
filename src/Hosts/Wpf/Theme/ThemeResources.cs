using System.Windows;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Theme;

/// <summary>
/// 在运行时切换 WPF 主题资源字典。界面元素全部通过 DynamicResource 引用画笔，
/// 因此替换字典后标题栏、状态栏等 WPF 原生区域会立即跟随。
/// </summary>
internal static class ThemeResources
{
    private static ResourceDictionary? _currentThemeDictionary;

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        var fileName = theme == AppTheme.Dark ? "Dark" : "Light";
        var source = new Uri(
            $"pack://application:,,,/SDemo.DesktopAgent;component/Themes/{fileName}.xaml",
            UriKind.Absolute);

        var dictionary = new ResourceDictionary { Source = source };
        var merged = Application.Current.Resources.MergedDictionaries;

        if (_currentThemeDictionary is not null && merged.Contains(_currentThemeDictionary))
        {
            merged[merged.IndexOf(_currentThemeDictionary)] = dictionary;
        }
        else
        {
            merged.Insert(0, dictionary);
        }

        _currentThemeDictionary = dictionary;
    }
}
