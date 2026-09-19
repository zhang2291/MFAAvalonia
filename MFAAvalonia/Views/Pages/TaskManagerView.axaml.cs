using Avalonia.Controls;
using Avalonia.Interactivity;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Views.Pages;

public partial class TaskManagerView : UserControl
{
    private InterfaceEditorDocument? _document;
    private bool _loadingFields;

    public TaskManagerView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadDocument();
    }

    private EditableTask? CurrentTask => TaskList.SelectedItem as EditableTask;
    private EditableOption? CurrentOption => OptionList.SelectedItem as EditableOption;
    private EditableOptionCase? CurrentCase => CaseList.SelectedItem as EditableOptionCase;

    private void RefreshTaskList()
    {
        var selected = TaskList.SelectedItem;
        var source = TaskList.ItemsSource;
        TaskList.ItemsSource = null;
        TaskList.ItemsSource = source;
        TaskList.SelectedItem = selected;
    }

    private void RefreshOptionList()
    {
        var selected = OptionList.SelectedItem;
        var source = OptionList.ItemsSource;
        OptionList.ItemsSource = null;
        OptionList.ItemsSource = source;
        OptionList.SelectedItem = selected;
    }

    private void RefreshCaseList()
    {
        var selected = CaseList.SelectedItem;
        var source = CaseList.ItemsSource;
        CaseList.ItemsSource = null;
        CaseList.ItemsSource = source;
        CaseList.SelectedItem = selected;
    }

    private void LoadDocument()
    {
        try
        {
            var projectRoot = MpeIntegrationService.ResolveProjectRoot();
            _document = InterfaceTaskEditorService.Load(projectRoot);
            ProjectPathText.Text = projectRoot;
            TaskList.ItemsSource = _document.Tasks;
            TaskList.SelectedIndex = _document.Tasks.Count > 0 ? 0 : -1;
            StatusText.Text = $"已读取 {_document.Tasks.Count} 个主页任务。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取 interface.json 失败：{ex.Message}";
            LoggerHelper.Error(StatusText.Text, ex);
        }
    }
    private void OnTaskSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadTaskFields(CurrentTask);
    }

    private void LoadTaskFields(EditableTask? task)
    {
        _loadingFields = true;
        try
        {
            EditorPanel.IsEnabled = task != null;
            if (task == null)
                return;

            TaskNameBox.Text = task.Name;
            TaskDescriptionBox.Text = task.Description;
            TaskDefaultCheck.IsChecked = task.DefaultCheck;
            TaskOfficialCheck.IsChecked = task.Resources.Contains("官服");
            TaskBilibiliCheck.IsChecked = task.Resources.Contains("B服");
            TaskGlobalCheck.IsChecked = task.Resources.Contains("国际服");
            OptionList.ItemsSource = task.Options;
            OptionList.SelectedIndex = task.Options.Count > 0 ? 0 : -1;
        }
        finally
        {
            _loadingFields = false;
        }
        LoadOptionFields(CurrentOption);
        RefreshPipelineChoices();
    }

    private void RefreshPipelineChoices()
    {
        // Updating ComboBox ItemsSource/SelectedItem raises SelectionChanged synchronously.
        // Keep the editor in loading mode for the whole refresh to avoid recursively
        // calling OnTaskEntrySelectionChanged -> RefreshPipelineChoices forever.
        var previousLoading = _loadingFields;
        _loadingFields = true;
        try
        {
            if (_document == null || CurrentTask is not { } task)
            {
                TaskEntryCombo.ItemsSource = null;
                ExistingPipelineCaseCombo.ItemsSource = null;
                OverrideSourcePipelineCombo.ItemsSource = null;
                OverrideTargetPipelineCombo.ItemsSource = null;
                OverrideNodeCombo.ItemsSource = null;
                return;
            }

            var choices = InterfaceTaskEditorService.GetPipelineNodes(_document, task.Resources);
            TaskEntryCombo.ItemsSource = choices;
            ExistingPipelineCaseCombo.ItemsSource = choices;
            OverrideSourcePipelineCombo.ItemsSource = choices;
            OverrideTargetPipelineCombo.ItemsSource = choices;
            OverrideNodeCombo.ItemsSource = choices;

            TaskEntryCombo.SelectedItem = choices.FirstOrDefault(item => item.Name == task.Entry);
            ExistingPipelineCaseCombo.SelectedIndex = choices.Count > 0 ? 0 : -1;
            OverrideSourcePipelineCombo.SelectedItem = choices.FirstOrDefault(item => item.Name == task.Entry)
                ?? choices.FirstOrDefault();
            OverrideTargetPipelineCombo.SelectedIndex = choices.Count > 1 ? 1 : (choices.Count > 0 ? 0 : -1);
            OverrideNodeCombo.SelectedIndex = choices.Count > 0 ? 0 : -1;
        }
        finally
        {
            _loadingFields = previousLoading;
        }
    }

    private void CommitTaskFields()
    {
        if (_loadingFields || CurrentTask is not { } task)
            return;

        task.Name = TaskNameBox.Text?.Trim() ?? string.Empty;
        task.Description = TaskDescriptionBox.Text ?? string.Empty;
        task.DefaultCheck = TaskDefaultCheck.IsChecked == true;
        task.Resources.Clear();
        if (TaskOfficialCheck.IsChecked == true) task.Resources.Add("官服");
        if (TaskBilibiliCheck.IsChecked == true) task.Resources.Add("B服");
        if (TaskGlobalCheck.IsChecked == true) task.Resources.Add("国际服");
        RefreshTaskList();
        RefreshPipelineChoices();
    }

    private void OnTaskFieldLostFocus(object? sender, RoutedEventArgs e) => CommitTaskFields();
    private void OnTaskFieldChanged(object? sender, RoutedEventArgs e) => CommitTaskFields();

    private void OnTaskEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingFields || CurrentTask is not { } task)
            return;
        if (TaskEntryCombo.SelectedItem is PipelineNodeChoice choice)
        {
            task.Entry = choice.Name;
            RefreshTaskList();
            RefreshPipelineChoices();
        }
    }

    private void OnAddTaskClick(object? sender, RoutedEventArgs e)
    {
        if (_document == null) return;
        CommitTaskFields();
        var task = InterfaceTaskEditorService.CreateTask();
        _document.Tasks.Add(task);
        TaskList.SelectedItem = task;
        StatusText.Text = "已新增任务，填写名称和 entry 后保存。";
    }

    private void OnDeleteTaskClick(object? sender, RoutedEventArgs e)
    {
        if (_document == null || CurrentTask is not { } task) return;
        var index = _document.Tasks.IndexOf(task);
        _document.Tasks.Remove(task);
        TaskList.SelectedIndex = _document.Tasks.Count == 0 ? -1 : Math.Min(index, _document.Tasks.Count - 1);
        StatusText.Text = $"已从待保存列表删除任务：{task.Name}";
    }

    private void OnMoveTaskUpClick(object? sender, RoutedEventArgs e)
    {
        if (_document == null || CurrentTask is not { } task) return;
        var index = _document.Tasks.IndexOf(task);
        if (index <= 0) return;
        _document.Tasks.Move(index, index - 1);
        TaskList.SelectedItem = task;
    }
    private void OnMoveTaskDownClick(object? sender, RoutedEventArgs e)
    {
        if (_document == null || CurrentTask is not { } task) return;
        var index = _document.Tasks.IndexOf(task);
        if (index < 0 || index >= _document.Tasks.Count - 1) return;
        _document.Tasks.Move(index, index + 1);
        TaskList.SelectedItem = task;
    }

    private void OnOptionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadOptionFields(CurrentOption);
    }

    private void LoadOptionFields(EditableOption? option)
    {
        _loadingFields = true;
        try
        {
            OptionEditorPanel.IsEnabled = option != null;
            if (option == null)
            {
                OptionNameBox.Text = string.Empty;
                OptionDescriptionBox.Text = string.Empty;
                OptionDefaultCaseCombo.ItemsSource = null;
                CaseList.ItemsSource = null;
                LoadCaseFields(null);
                return;
            }
            OptionNameBox.Text = option.Name;
            OptionDescriptionBox.Text = option.Description;
            CaseList.ItemsSource = option.Cases;
            CaseList.SelectedIndex = option.Cases.Count > 0 ? 0 : -1;
        }
        finally
        {
            _loadingFields = false;
        }
        RefreshDefaultCaseChoices(option);
        LoadCaseFields(CurrentCase);
    }

    private void RefreshDefaultCaseChoices(EditableOption? option)
    {
        if (option == null)
        {
            OptionDefaultCaseCombo.ItemsSource = null;
            return;
        }
        var values = new System.Collections.Generic.List<string> { "（不指定）" };
        values.AddRange(option.Cases.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
        if (!string.IsNullOrWhiteSpace(option.DefaultCase) && !values.Contains(option.DefaultCase))
            values.Add(option.DefaultCase);
        OptionDefaultCaseCombo.ItemsSource = values;
        OptionDefaultCaseCombo.SelectedItem = string.IsNullOrWhiteSpace(option.DefaultCase) ? "（不指定）" : option.DefaultCase;
    }

    private void OnOptionDefaultCaseSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingFields || CurrentOption is not { } option)
            return;
        var selected = OptionDefaultCaseCombo.SelectedItem as string;
        option.DefaultCase = selected == "（不指定）" ? string.Empty : selected ?? string.Empty;
    }

    private void OnAddOptionClick(object? sender, RoutedEventArgs e)
    {
        if (_document == null || CurrentTask is not { } task) return;
        var baseName = "新设置";
        var suffix = 1;
        var name = baseName + suffix;
        while (_document.OptionsByName.ContainsKey(name)) name = baseName + ++suffix;
        var option = InterfaceTaskEditorService.CreateOption(name);
        _document.OptionsByName[name] = option;
        task.Options.Add(option);
        OptionList.SelectedItem = option;
        StatusText.Text = "已新增任务设置，可继续添加 cases。";
    }

    private void OnDeleteOptionClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentTask is not { } task || CurrentOption is not { } option) return;
        var index = task.Options.IndexOf(option);
        task.Options.Remove(option);
        OptionList.SelectedIndex = task.Options.Count == 0 ? -1 : Math.Min(index, task.Options.Count - 1);
    }

    private void OnOptionNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_loadingFields || _document == null || CurrentOption is not { } option) return;
        var newName = OptionNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText.Text = "设置名称不能为空。";
            OptionNameBox.Text = option.Name;
            return;
        }

        var oldKey = _document.OptionsByName.FirstOrDefault(pair => ReferenceEquals(pair.Value, option)).Key;
        if (!string.Equals(oldKey, newName, StringComparison.Ordinal)
            && _document.OptionsByName.TryGetValue(newName, out var existing)
            && !ReferenceEquals(existing, option))
        {
            StatusText.Text = $"设置名称“{newName}”已存在。";
            OptionNameBox.Text = option.Name;
            return;
        }

        if (!string.IsNullOrEmpty(oldKey)) _document.OptionsByName.Remove(oldKey);
        option.Name = newName;
        _document.OptionsByName[newName] = option;
        RefreshOptionList();
    }

    private void OnOptionFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_loadingFields || CurrentOption is not { } option)
            return;
        option.Description = OptionDescriptionBox.Text ?? string.Empty;
    }

    private void OnCaseSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadCaseFields(CurrentCase);
    }

    private void LoadCaseFields(EditableOptionCase? optionCase)
    {
        _loadingFields = true;
        try
        {
            CaseEditorPanel.IsEnabled = optionCase != null;
            CaseNameBox.Text = optionCase?.Name ?? string.Empty;
            CaseIconBox.Text = optionCase?.Icon ?? string.Empty;
            PipelineOverrideBox.Text = optionCase?.PipelineOverrideJson ?? string.Empty;
        }
        finally
        {
            _loadingFields = false;
        }
    }

    private void OnAddCaseClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentOption is not { } option) return;
        var optionCase = InterfaceTaskEditorService.CreateCase();
        option.Cases.Add(optionCase);
        CaseList.SelectedItem = optionCase;
        RefreshDefaultCaseChoices(option);
    }

    private void OnAddCaseFromPipelineClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentOption is not { } option || ExistingPipelineCaseCombo.SelectedItem is not PipelineNodeChoice choice)
        {
            StatusText.Text = "请先选择一个已有 Pipeline 节点。";
            return;
        }

        var existing = option.Cases.FirstOrDefault(item => string.Equals(item.Name, choice.Name, StringComparison.Ordinal));
        if (existing != null)
        {
            CaseList.SelectedItem = existing;
            StatusText.Text = $"选项“{choice.Name}”已存在。";
            return;
        }

        var optionCase = InterfaceTaskEditorService.CreateCase();
        optionCase.Name = choice.Name;
        if (CurrentTask is { } task && !string.IsNullOrWhiteSpace(task.Entry))
        {
            optionCase.PipelineOverrideJson = new JObject
            {
                [task.Entry] = new JObject { ["next"] = choice.Name }
            }.ToString(Formatting.Indented);
        }
        option.Cases.Add(optionCase);
        CaseList.SelectedItem = optionCase;
        RefreshDefaultCaseChoices(option);
        StatusText.Text = $"已添加“{choice.Name}”，并自动设置任务入口跳转。";
    }

    private void OnDeleteCaseClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentOption is not { } option || CurrentCase is not { } optionCase) return;
        var index = option.Cases.IndexOf(optionCase);
        option.Cases.Remove(optionCase);
        CaseList.SelectedIndex = option.Cases.Count == 0 ? -1 : Math.Min(index, option.Cases.Count - 1);
        if (option.DefaultCase == optionCase.Name) option.DefaultCase = string.Empty;
        RefreshDefaultCaseChoices(option);
    }

    private void OnCaseFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_loadingFields || CurrentCase is not { } optionCase) return;
        optionCase.Name = CaseNameBox.Text?.Trim() ?? string.Empty;
        optionCase.Icon = CaseIconBox.Text?.Trim() ?? string.Empty;
        optionCase.PipelineOverrideJson = string.IsNullOrWhiteSpace(PipelineOverrideBox.Text)
            ? "{}"
            : PipelineOverrideBox.Text;
        RefreshCaseList();
    }

    private void OnApplyQuickOverrideClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentCase is not { } optionCase)
            return;

        var nodeName = (OverrideNodeCombo.SelectedItem as PipelineNodeChoice)?.Name ?? string.Empty;
        var fieldName = OverrideFieldBox.Text?.Trim() ?? string.Empty;
        var rawValue = OverrideValueBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(nodeName) || string.IsNullOrWhiteSpace(fieldName))
        {
            StatusText.Text = "快捷覆盖需要填写节点名和字段名。";
            return;
        }

        try
        {
            var root = string.IsNullOrWhiteSpace(PipelineOverrideBox.Text)
                ? new JObject()
                : JObject.Parse(PipelineOverrideBox.Text);
            if (root[nodeName] is not JObject node)
            {
                node = new JObject();
                root[nodeName] = node;
            }
            node[fieldName] = ParseQuickOverrideValue(rawValue);
            var formatted = root.ToString(Formatting.Indented);
            PipelineOverrideBox.Text = formatted;
            optionCase.PipelineOverrideJson = formatted;
            StatusText.Text = $"已写入覆盖：{nodeName}.{fieldName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"写入覆盖失败：{ex.Message}";
        }
    }

    private void OnApplyPipelineNextClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentCase is not { } optionCase)
            return;
        if (OverrideSourcePipelineCombo.SelectedItem is not PipelineNodeChoice source
            || OverrideTargetPipelineCombo.SelectedItem is not PipelineNodeChoice target)
        {
            StatusText.Text = "请选择来源节点和目标节点。";
            return;
        }
        try
        {
            var root = string.IsNullOrWhiteSpace(PipelineOverrideBox.Text)
                ? new JObject()
                : JObject.Parse(PipelineOverrideBox.Text);
            if (root[source.Name] is not JObject node)
            {
                node = new JObject();
                root[source.Name] = node;
            }
            node["next"] = target.Name;
            var formatted = root.ToString(Formatting.Indented);
            PipelineOverrideBox.Text = formatted;
            optionCase.PipelineOverrideJson = formatted;
            StatusText.Text = $"已设置：{source.Name} → {target.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"设置跳转失败：{ex.Message}";
        }
    }

    private static JToken ParseQuickOverrideValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return JValue.CreateNull();
        try
        {
            return JToken.Parse(value);
        }
        catch (JsonReaderException)
        {
            return new JValue(value);
        }
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        LoadDocument();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        SaveDocument(apply: false);
    }

    private void OnSaveApplyClick(object? sender, RoutedEventArgs e)
    {
        SaveDocument(apply: true);
    }

    private void SaveDocument(bool apply)
    {
        if (_document == null) return;
        CommitTaskFields();
        OnOptionNameLostFocus(null, new RoutedEventArgs());
        OnOptionFieldLostFocus(null, new RoutedEventArgs());
        OnCaseFieldLostFocus(null, new RoutedEventArgs());

        try
        {
            InterfaceTaskEditorService.Save(_document);
            if (!apply)
            {
                StatusText.Text = "interface.json 已保存。";
                ToastHelper.Info("任务管理", "interface.json 已保存。" );
                return;
            }

            bool applied;
            string message;
            if (string.Equals(
                    Path.GetFullPath(_document.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(AppPaths.DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                applied = MaaProcessor.ReloadInterfaceAndTasks(out message);
            }
            else
            {
                applied = false;
                message = $"interface.json 已保存到 {_document.ProjectRoot}，但当前运行资源目录是 {AppPaths.DataRoot}。请从该 MBCCtools 目录启动后再热应用。";
            }

            StatusText.Text = message;
            if (applied)
                ToastHelper.Info("任务管理", message);
            else
                ToastHelper.Warn("任务管理", message);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
            ToastHelper.Error("任务管理", StatusText.Text);
            LoggerHelper.Error(StatusText.Text, ex);
        }
    }
}
