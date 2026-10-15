using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System;
using System.Threading.Tasks;

namespace MFAAvalonia.Views.Pages;

public static class GachaCategoryDialogs
{
    private static Window Create(string title, double width)
    {
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        return new Window { Title = title, Width = width, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse(dark ? "#23252D" : "#F8F9FC")),
            Foreground = new SolidColorBrush(Color.Parse(dark ? "#F1F3F9" : "#202633")) };
    }

    private static Button Button(string text) => new() { Content = text, FontSize = 14,
        Height = 36, MinHeight = 36, Padding = new Thickness(16, 5), Margin = new Thickness(8, 0, 0, 0) };

    public static Window Add(Func<string, Task> save)
    {
        var dialog = Create("添加池子类型", 420);
        var input = new TextBox { Name = "CategoryName", Watermark = "例如：四合一", FontSize = 14, MaxLength = 40 };
        var error = new TextBlock { Name = "ValidationMessage", FontSize = 12, Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var cancel = Button("取消"); cancel.Name = "Cancel"; cancel.IsCancel = true;
        var add = Button("添加"); add.Name = "Add"; add.IsEnabled = false; add.IsDefault = true;
        bool saving = false;
        input.TextChanged += (_, _) => add.IsEnabled = !saving && !string.IsNullOrWhiteSpace(input.Text);
        cancel.Click += (_, _) => dialog.Close((string?)null);
        add.Click += async (_, _) => {
            saving = true; add.IsEnabled = cancel.IsEnabled = input.IsEnabled = false; error.IsVisible = false;
            try { var name = input.Text!.Trim(); await save(name); saving = false; dialog.Close(name); }
            catch (Exception e) { error.Text = e.Message; error.IsVisible = true; }
            finally { saving = false; cancel.IsEnabled = input.IsEnabled = true; add.IsEnabled = !string.IsNullOrWhiteSpace(input.Text); }
        };
        dialog.Closing += (_, e) => { if (saving) e.Cancel = true; };
        dialog.Opened += (_, _) => input.Focus();
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children = {
            new TextBlock { Text = "类型名称", FontSize = 14, FontWeight = FontWeight.SemiBold }, input, error,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0), Children = { cancel, add } }
        }};
        return dialog;
    }

    public static Window Delete(string categoryName)
    {
        var dialog = Create("删除池子类型", 500);
        var cancel = Button("取消"); cancel.Name = "Cancel"; cancel.IsCancel = true;
        var backup = Button("删除并备份"); backup.Name = "Backup"; backup.IsDefault = true;
        var permanent = Button("永久删除"); permanent.Name = "Permanent"; permanent.Foreground = Brushes.IndianRed;
        cancel.Click += (_, _) => dialog.Close("cancel");
        backup.Click += (_, _) => dialog.Close("backup");
        permanent.Click += (_, _) => dialog.Close("permanent");
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children = {
            new TextBlock { Text = $"删除“{categoryName}”？", FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "删除并备份：记录移到备份目录，可恢复。", FontSize = 13, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "永久删除：清除当前记录和自动报告，不留新备份，无法撤销。", FontSize = 13, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0), Children = { cancel, backup, permanent } }
        }};
        return dialog;
    }
}
