using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WpfApp1
{
    public partial class MainWindow
    {
        private async Task ExecuteCreateImageWizardAsync()
        {
            var request = ShowImageCreateWizard();
            if (request == null)
            {
                return;
            }

            await BuildImageFromWizardAsync(request);
        }

        private ImageCreateWizardRequest ShowImageCreateWizard()
        {
            var state = CreateInitialImageWizardState();
            var result = default(ImageCreateWizardRequest);
            var currentStep = 1;
            var isSwitchingTemplate = false;
            var isSynchronizingBase = false;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "新建镜像向导",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            };
            var stepText = new TextBlock
            {
                Margin = new Thickness(0, 5, 0, 12),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };
            var header = new StackPanel();
            header.Children.Add(title);
            header.Children.Add(stepText);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var pageHost = new Grid();
            Grid.SetRow(pageHost, 1);
            root.Children.Add(pageHost);

            // 第一步：选择 Dockerfile 来源，并允许用户直接编辑。
            var step1 = new Grid();
            step1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            step1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            step1.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var sourcePanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            sourcePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sourcePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sourcePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sourcePanel.Children.Add(new TextBlock
            {
                Text = "Dockerfile 来源：",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            });
            var templateCombo = new ComboBox
            {
                MinWidth = 480,
                Height = 32,
                Margin = new Thickness(10, 0, 10, 0),
                DisplayMemberPath = "DisplayName"
            };
            Grid.SetColumn(templateCombo, 1);
            sourcePanel.Children.Add(templateCombo);
            var importButton = new Button
            {
                Content = "导入已有 Dockerfile",
                Height = 32,
                MinWidth = 150,
                Padding = new Thickness(12, 0, 12, 0)
            };
            Grid.SetColumn(importButton, 2);
            sourcePanel.Children.Add(importButton);
            Grid.SetRow(sourcePanel, 0);
            step1.Children.Add(sourcePanel);

            var sourceHint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };
            Grid.SetRow(sourceHint, 1);
            step1.Children.Add(sourceHint);

            var step1Editor = CreateWizardDockerfileEditor();
            Grid.SetRow(step1Editor, 2);
            step1.Children.Add(step1Editor);
            pageHost.Children.Add(step1);

            // 第二步：以 Dockerfile 为事实来源，匹配基础镜像与程序包。
            var step2 = new Grid { Visibility = Visibility.Collapsed };
            step2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            step2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            step2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            step2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            step2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });

            var basePanel = new Grid { Margin = new Thickness(0, 0, 12, 10) };
            basePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            basePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var baseLabel = new TextBlock
            {
                Text = "本机基础镜像：",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };
            basePanel.Children.Add(baseLabel);
            var baseImageCombo = new ComboBox
            {
                Height = 32,
                Margin = new Thickness(10, 0, 0, 0),
                IsEditable = true
            };
            Grid.SetColumn(baseImageCombo, 1);
            basePanel.Children.Add(baseImageCombo);
            Grid.SetColumn(basePanel, 0);
            Grid.SetRow(basePanel, 0);
            step2.Children.Add(basePanel);

            var reparseButton = new Button
            {
                Content = "重新解析 Dockerfile",
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(12, 0, 0, 10)
            };
            Grid.SetColumn(reparseButton, 1);
            Grid.SetRow(reparseButton, 0);
            step2.Children.Add(reparseButton);

            var dependencyPanel = new Grid { Margin = new Thickness(0, 0, 12, 10) };
            dependencyPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dependencyPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            dependencyPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dependencyPanel.Children.Add(CreateWizardSectionHeader("Dockerfile 依赖与本地匹配"));
            var packageGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                Margin = new Thickness(0, 8, 0, 8),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
            packageGrid.Columns.Add(new DataGridTextColumn { Header = "状态", Width = 82, Binding = new Binding("StatusText") });
            packageGrid.Columns.Add(new DataGridTextColumn { Header = "构建上下文路径", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding("ContextPath") });
            packageGrid.Columns.Add(new DataGridTextColumn { Header = "本地文件", Width = new DataGridLength(1.1, DataGridLengthUnitType.Star), Binding = new Binding("LocalFileName") });
            packageGrid.Columns.Add(new DataGridTextColumn { Header = "关系", Width = 90, Binding = new Binding("RelationText") });
            packageGrid.Columns.Add(new DataGridTextColumn { Header = "版本", Width = 90, Binding = new Binding("VersionText") });
            Grid.SetRow(packageGrid, 1);
            dependencyPanel.Children.Add(packageGrid);

            var packageButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var choosePackageButton = new Button { Content = "选择/替换文件", Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0) };
            var addPackageButton = new Button { Content = "添加程序包", Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0) };
            var removePackageButton = new Button { Content = "移除所选", Height = 30, Padding = new Thickness(12, 0, 12, 0) };
            packageButtons.Children.Add(choosePackageButton);
            packageButtons.Children.Add(addPackageButton);
            packageButtons.Children.Add(removePackageButton);
            Grid.SetRow(packageButtons, 2);
            dependencyPanel.Children.Add(packageButtons);
            Grid.SetColumn(dependencyPanel, 0);
            Grid.SetRow(dependencyPanel, 1);
            step2.Children.Add(dependencyPanel);

            var step2EditorPanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            step2EditorPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            step2EditorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            step2EditorPanel.Children.Add(CreateWizardSectionHeader("Dockerfile（手工内容不会被静默覆盖）"));
            var step2Editor = CreateWizardDockerfileEditor();
            step2Editor.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(step2Editor, 1);
            step2EditorPanel.Children.Add(step2Editor);
            Grid.SetColumn(step2EditorPanel, 1);
            Grid.SetRow(step2EditorPanel, 1);
            step2.Children.Add(step2EditorPanel);

            var diagnosticsPanel = new Grid();
            diagnosticsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            diagnosticsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            diagnosticsPanel.Children.Add(CreateWizardSectionHeader("集中诊断：缺失、多余、配套、冲突与版本"));
            var diagnosticsBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252))
            };
            Grid.SetRow(diagnosticsBox, 1);
            diagnosticsPanel.Children.Add(diagnosticsBox);
            Grid.SetColumnSpan(diagnosticsPanel, 2);
            Grid.SetRow(diagnosticsPanel, 2);
            step2.Children.Add(diagnosticsPanel);
            pageHost.Children.Add(step2);

            // 第三步：镜像标识、输出目录、最终 Dockerfile 与摘要。
            var step3 = new Grid { Visibility = Visibility.Collapsed };
            step3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
            step3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            step3.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var settingsPanel = new Grid { Margin = new Thickness(0, 0, 14, 0) };
            settingsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            settingsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            settingsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            settingsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            settingsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            settingsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            settingsPanel.Children.Add(CreateWizardSectionHeader("构建设置"));

            var nameBox = CreateWizardLabeledTextBox(settingsPanel, 1, "镜像名称（Repository）", "local");
            var tagBox = CreateWizardLabeledTextBox(settingsPanel, 2, "Tag", "custom_image_" + DateTime.Now.ToString("yyyyMMddHHmmss"));

            var outputPanel = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            outputPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outputPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outputPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outputPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var outputLabel = new TextBlock { Text = "输出目录", FontWeight = FontWeights.SemiBold };
            Grid.SetColumnSpan(outputLabel, 2);
            outputPanel.Children.Add(outputLabel);
            var outputBox = new TextBox
            {
                Height = 32,
                Margin = new Thickness(0, 5, 8, 0),
                Text = Path.Combine(GetDockerBuildWorkspaceRoot(), "image_outputs")
            };
            Grid.SetRow(outputBox, 1);
            outputPanel.Children.Add(outputBox);
            var browseOutputButton = new Button { Content = "浏览...", Height = 32, MinWidth = 78, Margin = new Thickness(0, 5, 0, 0) };
            Grid.SetColumn(browseOutputButton, 1);
            Grid.SetRow(browseOutputButton, 1);
            outputPanel.Children.Add(browseOutputButton);
            Grid.SetRow(outputPanel, 3);
            settingsPanel.Children.Add(outputPanel);

            var validateButton = new Button
            {
                Content = "重新校验并刷新摘要",
                Height = 32,
                Margin = new Thickness(0, 12, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 0, 14, 0)
            };
            Grid.SetRow(validateButton, 4);
            settingsPanel.Children.Add(validateButton);
            var summaryBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252))
            };
            Grid.SetRow(summaryBox, 5);
            settingsPanel.Children.Add(summaryBox);
            Grid.SetColumn(settingsPanel, 0);
            step3.Children.Add(settingsPanel);

            var finalPanel = new Grid();
            finalPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            finalPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            finalPanel.Children.Add(CreateWizardSectionHeader("最终 Dockerfile（仍可编辑，构建前会重新解析）"));
            var finalEditor = CreateWizardDockerfileEditor();
            finalEditor.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(finalEditor, 1);
            finalPanel.Children.Add(finalEditor);
            Grid.SetColumn(finalPanel, 1);
            step3.Children.Add(finalPanel);
            pageHost.Children.Add(step3);

            var backButton = new Button { Content = "上一步", Width = 92, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
            var nextButton = new Button { Content = "下一步", Width = 112, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "取消", Width = 92, Height = 34, IsCancel = true };
            var navigation = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            navigation.Children.Add(backButton);
            navigation.Children.Add(nextButton);
            navigation.Children.Add(cancelButton);
            Grid.SetRow(navigation, 2);
            root.Children.Add(navigation);

            var dialog = new Window
            {
                Title = "新建镜像向导",
                Width = 1220,
                Height = 850,
                MinWidth = 1040,
                MinHeight = 720,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };
            EnsureWindowCenteredOnOwner(dialog);

            var choices = ReadImageWizardTemplateChoices(state);
            templateCombo.ItemsSource = choices;
            templateCombo.SelectedIndex = 0;
            step1Editor.Text = state.DockerfileText;
            state.LastLoadedSourceText = state.DockerfileText;
            sourceHint.Text = choices.Count > 0 ? choices[0].Description : string.Empty;

            Action refreshStep2 = () =>
            {
                state.DockerfileText = step2Editor.Text ?? string.Empty;
                AnalyzeImageWizardState(state);
                packageGrid.ItemsSource = null;
                packageGrid.ItemsSource = state.PackageBindings;
                diagnosticsBox.Text = BuildImageWizardDiagnosticsText(state);

                isSynchronizingBase = true;
                try
                {
                    baseImageCombo.ItemsSource = state.LocalImageRepoTags;
                    var primary = state.FromImages.FirstOrDefault() ?? string.Empty;
                    baseImageCombo.Text = primary;
                }
                finally
                {
                    isSynchronizingBase = false;
                }
            };

            Action refreshStep3 = () =>
            {
                state.DockerfileText = finalEditor.Text ?? string.Empty;
                AnalyzeImageWizardState(state);
                summaryBox.Text = BuildImageWizardSummary(state, nameBox.Text, tagBox.Text, outputBox.Text);
            };

            Action<int> showStep = null;
            showStep = stepNumber =>
            {
                currentStep = Math.Max(1, Math.Min(3, stepNumber));
                step1.Visibility = currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
                step2.Visibility = currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
                step3.Visibility = currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
                backButton.IsEnabled = currentStep > 1;
                nextButton.Content = currentStep == 3 ? "开始构建" : "下一步";
                stepText.Text = currentStep == 1
                    ? "第 1/3 步：选择预置模板、导入已有 Dockerfile 或创建空白 Dockerfile"
                    : currentStep == 2
                        ? "第 2/3 步：解析 Dockerfile，并调整基础镜像、程序包及依赖关系"
                        : "第 3/3 步：设置镜像名称、Tag 和输出目录，确认最终构建摘要";

                if (currentStep == 2)
                {
                    step2Editor.Text = state.DockerfileText;
                    refreshStep2();
                }
                else if (currentStep == 3)
                {
                    finalEditor.Text = state.DockerfileText;
                    refreshStep3();
                }
            };

            templateCombo.SelectionChanged += (_, __) =>
            {
                if (isSwitchingTemplate)
                {
                    return;
                }

                var choice = templateCombo.SelectedItem as ImageWizardTemplateChoice;
                if (choice == null)
                {
                    return;
                }

                if (!ConfirmDiscardManualDockerfileChanges(dialog, step1Editor.Text, state.LastLoadedSourceText))
                {
                    isSwitchingTemplate = true;
                    templateCombo.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Content, state.LastLoadedSourceText, StringComparison.Ordinal));
                    if (templateCombo.SelectedItem == null)
                    {
                        templateCombo.SelectedIndex = 0;
                    }
                    isSwitchingTemplate = false;
                    return;
                }

                step1Editor.Text = choice.Content;
                state.LastLoadedSourceText = choice.Content;
                state.SourceDescription = choice.DisplayName;
                state.ImportedDockerfileDirectory = string.Empty;
                sourceHint.Text = choice.Description;
            };

            importButton.Click += (_, __) =>
            {
                if (!ConfirmDiscardManualDockerfileChanges(dialog, step1Editor.Text, state.LastLoadedSourceText))
                {
                    return;
                }

                var fileDialog = new OpenFileDialog
                {
                    Title = "导入已有 Dockerfile",
                    Filter = "Dockerfile|Dockerfile;*.dockerfile|所有文件 (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (fileDialog.ShowDialog(dialog) != true)
                {
                    return;
                }

                try
                {
                    var content = File.ReadAllText(fileDialog.FileName, Encoding.UTF8);
                    step1Editor.Text = content;
                    state.LastLoadedSourceText = content;
                    state.SourceDescription = "导入：" + fileDialog.FileName;
                    state.ImportedDockerfileDirectory = Path.GetDirectoryName(fileDialog.FileName) ?? string.Empty;
                    sourceHint.Text = state.SourceDescription;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "读取 Dockerfile 失败：" + ex.Message, "新建镜像", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            reparseButton.Click += (_, __) => refreshStep2();

            baseImageCombo.SelectionChanged += (_, __) =>
            {
                if (isSynchronizingBase || currentStep != 2)
                {
                    return;
                }

                var selectedBase = (baseImageCombo.SelectedItem as string ?? baseImageCombo.Text ?? string.Empty).Trim();
                var declaredBase = state.FromImages.FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(selectedBase) || string.Equals(selectedBase, declaredBase, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var answer = MessageBox.Show(
                    dialog,
                    string.Format("Dockerfile 当前 FROM 为：\n{0}\n\n是否同步修改为：\n{1}\n\n选择“否”将保留手工 Dockerfile 内容。", declaredBase, selectedBase),
                    "同步修改 FROM",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    step2Editor.Text = ReplaceFirstFromInstruction(step2Editor.Text, selectedBase);
                    state.DockerfileText = step2Editor.Text;
                    refreshStep2();
                }
                else
                {
                    isSynchronizingBase = true;
                    baseImageCombo.Text = declaredBase;
                    isSynchronizingBase = false;
                }
            };

            choosePackageButton.Click += (_, __) =>
            {
                var item = packageGrid.SelectedItem as ImageWizardPackageBinding;
                if (item == null)
                {
                    MessageBox.Show(dialog, "请先选择一条依赖。", "新建镜像", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var fileDialog = new OpenFileDialog
                {
                    Title = "为 " + item.ContextPath + " 选择本地文件",
                    CheckFileExists = true,
                    Multiselect = false,
                    Filter = "程序包|*.tar.gz;*.tgz;*.tar;*.zip;*.sh;*.txt;*.jar|所有文件 (*.*)|*.*"
                };
                if (fileDialog.ShowDialog(dialog) != true)
                {
                    return;
                }

                state.ManualPackageMappings[item.ContextPath] = fileDialog.FileName;
                state.DisabledAutoMatchContexts.Remove(item.ContextPath);
                refreshStep2();
            };

            addPackageButton.Click += (_, __) =>
            {
                var fileDialog = new OpenFileDialog
                {
                    Title = "添加程序包",
                    CheckFileExists = true,
                    Multiselect = true,
                    Filter = "程序包|*.tar.gz;*.tgz;*.tar;*.zip;*.sh;*.txt;*.jar|所有文件 (*.*)|*.*"
                };
                if (fileDialog.ShowDialog(dialog) != true || fileDialog.FileNames.Length == 0)
                {
                    return;
                }

                var answer = MessageBox.Show(
                    dialog,
                    "是否把新增程序包同步写入 Dockerfile 的 COPY 指令？\n\n选择“否”会保留 Dockerfile 原文，并将这些文件标记为多余包。",
                    "同步修改 COPY",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                {
                    return;
                }

                for (var i = 0; i < fileDialog.FileNames.Length; i++)
                {
                    var path = fileDialog.FileNames[i];
                    if (!state.ExtraPackagePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        state.ExtraPackagePaths.Add(path);
                    }
                }

                if (answer == MessageBoxResult.Yes)
                {
                    step2Editor.Text = AppendCopyInstructions(step2Editor.Text, fileDialog.FileNames);
                    state.DockerfileText = step2Editor.Text;
                }
                refreshStep2();
            };

            removePackageButton.Click += (_, __) =>
            {
                var selectedItems = packageGrid.SelectedItems.Cast<object>().Select(o => o as ImageWizardPackageBinding).Where(o => o != null).ToList();
                if (selectedItems.Count == 0)
                {
                    MessageBox.Show(dialog, "请先选择要移除的依赖或程序包。", "新建镜像", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var requiredItems = selectedItems.Where(i => i.IsRequired).ToList();
                var synchronize = MessageBoxResult.No;
                if (requiredItems.Count > 0)
                {
                    synchronize = MessageBox.Show(
                        dialog,
                        "所选内容包含 Dockerfile 的 COPY/ADD 依赖。是否同步删除对应指令？\n\n选择“否”只会取消本地文件匹配，Dockerfile 将保持不变并显示缺失依赖。",
                        "同步修改 COPY/ADD",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);
                    if (synchronize == MessageBoxResult.Cancel)
                    {
                        return;
                    }
                }

                for (var i = 0; i < selectedItems.Count; i++)
                {
                    var item = selectedItems[i];
                    if (item.IsRequired)
                    {
                        if (synchronize == MessageBoxResult.Yes)
                        {
                            step2Editor.Text = RemoveDockerfileInstructionsForSource(step2Editor.Text, item.ContextPath);
                        }
                        else
                        {
                            state.ManualPackageMappings.Remove(item.ContextPath);
                            state.DisabledAutoMatchContexts.Add(item.ContextPath);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(item.SourcePath))
                    {
                        state.ExtraPackagePaths.RemoveAll(p => string.Equals(p, item.SourcePath, StringComparison.OrdinalIgnoreCase));
                    }
                }

                state.DockerfileText = step2Editor.Text;
                refreshStep2();
            };

            browseOutputButton.Click += (_, __) =>
            {
                using (var folderDialog = new Forms.FolderBrowserDialog())
                {
                    folderDialog.Description = "选择镜像 tar 输出目录";
                    folderDialog.SelectedPath = Directory.Exists(outputBox.Text) ? outputBox.Text : GetDockerBuildWorkspaceRoot();
                    if (folderDialog.ShowDialog() == Forms.DialogResult.OK)
                    {
                        outputBox.Text = folderDialog.SelectedPath;
                    }
                }
            };

            validateButton.Click += (_, __) => refreshStep3();

            backButton.Click += (_, __) =>
            {
                if (currentStep == 2)
                {
                    state.DockerfileText = step2Editor.Text ?? string.Empty;
                    step1Editor.Text = state.DockerfileText;
                    showStep(1);
                }
                else if (currentStep == 3)
                {
                    state.DockerfileText = finalEditor.Text ?? string.Empty;
                    step2Editor.Text = state.DockerfileText;
                    showStep(2);
                }
            };

            nextButton.Click += (_, __) =>
            {
                if (currentStep == 1)
                {
                    state.DockerfileText = step1Editor.Text ?? string.Empty;
                    showStep(2);
                    return;
                }

                if (currentStep == 2)
                {
                    state.DockerfileText = step2Editor.Text ?? string.Empty;
                    AnalyzeImageWizardState(state);
                    showStep(3);
                    return;
                }

                state.DockerfileText = finalEditor.Text ?? string.Empty;
                AnalyzeImageWizardState(state);
                var errors = ValidateImageWizardForBuild(state, nameBox.Text, tagBox.Text, outputBox.Text);
                summaryBox.Text = BuildImageWizardSummary(state, nameBox.Text, tagBox.Text, outputBox.Text);
                if (errors.Count > 0)
                {
                    MessageBox.Show(
                        dialog,
                        "构建校验未通过：\n\n" + string.Join("\n", errors.Select(e => "• " + e)),
                        "新建镜像",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                result = new ImageCreateWizardRequest(
                    state.DockerfileText,
                    (nameBox.Text ?? string.Empty).Trim(),
                    (tagBox.Text ?? string.Empty).Trim(),
                    NormalizeLocalPath(outputBox.Text),
                    state.PackageBindings.Select(CloneWizardPackageBinding).ToList(),
                    state.Diagnostics.ToList());
                dialog.DialogResult = true;
            };

            showStep(1);
            return dialog.ShowDialog() == true ? result : null;
        }

        private static TextBox CreateWizardDockerfileEditor()
        {
            return new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Background = Brushes.White
            };
        }

        private static TextBlock CreateWizardSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(34, 50, 77))
            };
        }

        private static TextBox CreateWizardLabeledTextBox(Grid parent, int row, string label, string initialValue)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            var box = new TextBox { Height = 32, Margin = new Thickness(0, 5, 0, 0), Text = initialValue ?? string.Empty };
            panel.Children.Add(box);
            Grid.SetRow(panel, row);
            parent.Children.Add(panel);
            return box;
        }

        private static bool ConfirmDiscardManualDockerfileChanges(Window owner, string currentText, string loadedText)
        {
            if (string.Equals(currentText ?? string.Empty, loadedText ?? string.Empty, StringComparison.Ordinal))
            {
                return true;
            }

            return MessageBox.Show(
                owner,
                "当前 Dockerfile 包含手工修改。继续将替换这些内容，是否确认？",
                "替换 Dockerfile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private ImageCreateWizardState CreateInitialImageWizardState()
        {
            var state = new ImageCreateWizardState();
            var selectedBase = SourceImageGrid == null ? null : SourceImageGrid.SelectedItem as ImageComposeRow;
            var baseRepoTag = NormalizeRepoTag(selectedBase == null ? string.Empty : selectedBase.RepoTag);
            if (string.IsNullOrWhiteSpace(baseRepoTag))
            {
                baseRepoTag = "ubuntu:20.04";
            }

            var selectedPackages = GetSelectedProgramPackages();
            for (var i = 0; i < selectedPackages.Count; i++)
            {
                var sourcePath = ResolvePackageSourcePath(selectedPackages[i]);
                if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                {
                    state.ExtraPackagePaths.Add(sourcePath);
                }
            }

            if (selectedPackages.Count > 0)
            {
                state.DockerfileText = ReplaceFirstFromInstruction(BuildGeneratedDockerfile(baseRepoTag, selectedPackages), baseRepoTag);
                state.SourceDescription = "根据主页面已有选择自动生成（仅作为预填，可在向导中调整）";
            }
            else
            {
                state.DockerfileText = "FROM " + baseRepoTag + Environment.NewLine + Environment.NewLine + "WORKDIR /home" + Environment.NewLine;
                state.SourceDescription = "空白向导的最小 Dockerfile（主页面未选择程序包）";
            }

            return state;
        }

        private static List<ImageWizardTemplateChoice> ReadImageWizardTemplateChoices(ImageCreateWizardState state)
        {
            var choices = new List<ImageWizardTemplateChoice>
            {
                new ImageWizardTemplateChoice(
                    "主页面预填/自动生成",
                    state.DockerfileText,
                    state.SourceDescription)
            };

            var templateDir = GetTemplateDockerfileDirectory();
            if (Directory.Exists(templateDir))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(templateDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                catch
                {
                    files = new string[0];
                }

                for (var i = 0; i < files.Length; i++)
                {
                    try
                    {
                        choices.Add(new ImageWizardTemplateChoice(
                            "预置模板：" + Path.GetFileName(files[i]),
                            File.ReadAllText(files[i], Encoding.UTF8),
                            "模板文件：" + files[i]));
                    }
                    catch
                    {
                    }
                }
            }

            choices.Add(new ImageWizardTemplateChoice(
                "创建空白 Dockerfile",
                string.Empty,
                "从空白内容开始；进入下一步后会提示缺少 FROM，补充完成后才允许构建。"));
            return choices;
        }

        private void AnalyzeImageWizardState(ImageCreateWizardState state)
        {
            state.Diagnostics.Clear();
            state.FromImages.Clear();
            state.PackageBindings.Clear();

            if (state.LocalImageRepoTags.Count == 0)
            {
                var localImages = ReadDockerImages(null);
                state.LocalImageRepoTags.AddRange(localImages
                    .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Repository) && !string.IsNullOrWhiteSpace(i.Tag) &&
                                !string.Equals(i.Repository, "<none>", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(i.Tag, "<none>", StringComparison.OrdinalIgnoreCase))
                    .Select(i => NormalizeRepoTag(i.Repository + ":" + i.Tag))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
            }

            var references = ParseImageWizardDockerfile(state.DockerfileText);
            state.FromImages.AddRange(references.FromImages);
            if (references.FromImages.Count == 0)
            {
                AddWizardDiagnostic(state, WizardDiagnosticSeverity.Error, "基础镜像", "Dockerfile 缺少 FROM 指令。");
            }

            for (var i = 0; i < references.FromImages.Count; i++)
            {
                var image = references.FromImages[i];
                if (string.Equals(image, "scratch", StringComparison.OrdinalIgnoreCase))
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Info, "基础镜像", "scratch 是 Docker 内置空白基础镜像，无需本机镜像匹配。");
                    continue;
                }
                if (image.IndexOf('$') >= 0)
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Error, "基础镜像", "FROM 使用变量，当前无法解析本机匹配：" + image);
                    continue;
                }

                if (state.LocalImageRepoTags.Contains(image, StringComparer.OrdinalIgnoreCase))
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Info, "基础镜像", "本机已匹配：" + image);
                    continue;
                }

                var repository = GetDockerRepositoryPart(image);
                var sameRepository = state.LocalImageRepoTags
                    .Where(t => string.Equals(GetDockerRepositoryPart(t), repository, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (sameRepository.Count > 0)
                {
                    AddWizardDiagnostic(
                        state,
                        WizardDiagnosticSeverity.Error,
                        "版本不匹配",
                        string.Format("Dockerfile 需要 {0}，本机同名镜像版本为：{1}", image, string.Join("、", sameRepository)));
                }
                else
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Error, "缺失基础镜像", "本机不存在：" + image);
                }
            }

            var localPackages = ReadLocalProgramPackages();
            var usedSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < references.PackageSources.Count; i++)
            {
                var source = references.PackageSources[i];
                if (source.IsExternalOrStage)
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Info, source.Instruction, "无需本地程序包匹配：" + source.ContextPath);
                    continue;
                }

                if (source.IsDirectory)
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Error, "目录依赖", source.ContextPath + " 是目录依赖，请改为明确文件或拆分 COPY/ADD。");
                    state.PackageBindings.Add(new ImageWizardPackageBinding(source.ContextPath, string.Empty, true, source.Instruction, source.LineNumber));
                    continue;
                }

                if (source.HasWildcard)
                {
                    var wildcardMatches = localPackages
                        .Where(p => WildcardMatches(Path.GetFileName(source.ContextPath), p.Name))
                        .ToList();
                    if (wildcardMatches.Count == 0)
                    {
                        state.PackageBindings.Add(new ImageWizardPackageBinding(source.ContextPath, string.Empty, true, source.Instruction, source.LineNumber));
                        AddWizardDiagnostic(state, WizardDiagnosticSeverity.Error, "缺失程序包", "没有文件匹配通配符：" + source.ContextPath);
                    }
                    else
                    {
                        for (var matchIndex = 0; matchIndex < wildcardMatches.Count; matchIndex++)
                        {
                            var matchPath = ResolvePackageSourcePath(wildcardMatches[matchIndex]);
                            var binding = new ImageWizardPackageBinding(wildcardMatches[matchIndex].Name, matchPath, true, source.Instruction + "（通配符）", source.LineNumber);
                            ApplyWizardPackageVersion(binding);
                            state.PackageBindings.Add(binding);
                            usedSourcePaths.Add(matchPath);
                        }
                    }
                    continue;
                }

                var contextPath = NormalizeWizardContextPath(source.ContextPath);
                string selectedPath;
                if (!state.ManualPackageMappings.TryGetValue(contextPath, out selectedPath) || !File.Exists(selectedPath))
                {
                    selectedPath = string.Empty;
                    if (!state.DisabledAutoMatchContexts.Contains(contextPath))
                    {
                        selectedPath = ResolveImportedDockerfileSource(state.ImportedDockerfileDirectory, contextPath);
                        var expectedName = Path.GetFileName(contextPath);
                        var matched = string.IsNullOrWhiteSpace(selectedPath)
                            ? localPackages.FirstOrDefault(p => string.Equals(p.Name, expectedName, StringComparison.OrdinalIgnoreCase))
                            : null;
                        if (matched != null && string.IsNullOrWhiteSpace(selectedPath))
                        {
                            selectedPath = ResolvePackageSourcePath(matched);
                        }

                        if (string.IsNullOrWhiteSpace(selectedPath))
                        {
                            selectedPath = state.ExtraPackagePaths.FirstOrDefault(p =>
                                File.Exists(p) && string.Equals(Path.GetFileName(p), expectedName, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                        }
                    }
                }

                var packageBinding = new ImageWizardPackageBinding(contextPath, selectedPath, true, source.Instruction, source.LineNumber);
                ApplyWizardPackageVersion(packageBinding);
                state.PackageBindings.Add(packageBinding);
                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    usedSourcePaths.Add(selectedPath);
                    state.ManualPackageMappings[contextPath] = selectedPath;
                }
                else
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Error, "缺失程序包", contextPath + " 未匹配到本地文件。");
                }
            }

            for (var i = 0; i < state.ExtraPackagePaths.Count; i++)
            {
                var path = state.ExtraPackagePaths[i];
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || usedSourcePaths.Contains(path))
                {
                    continue;
                }

                var extra = new ImageWizardPackageBinding(Path.GetFileName(path), path, false, "未被 Dockerfile 引用", 0);
                ApplyWizardPackageVersion(extra);
                state.PackageBindings.Add(extra);
                AddWizardDiagnostic(state, WizardDiagnosticSeverity.Warning, "多余程序包", Path.GetFileName(path) + " 已选择，但 Dockerfile 未引用。");
            }

            ApplyWizardTemplateRules(state);
            ApplyWizardVersionRules(state);
            if (state.Diagnostics.Count == 0)
            {
                AddWizardDiagnostic(state, WizardDiagnosticSeverity.Info, "校验", "未发现依赖问题。");
            }
        }

        private static ImageWizardDockerfileReferences ParseImageWizardDockerfile(string dockerfileText)
        {
            var result = new ImageWizardDockerfileReferences();
            var logicalInstructions = BuildDockerfileLogicalInstructions(dockerfileText);
            for (var i = 0; i < logicalInstructions.Count; i++)
            {
                var instruction = logicalInstructions[i];
                var text = StripDockerfileComment(instruction.Text).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var fromMatch = Regex.Match(text, @"^FROM\s+(?:(?:--\S+)\s+)*(?<image>""[^""]+""|\S+)", RegexOptions.IgnoreCase);
                if (fromMatch.Success)
                {
                    result.FromImages.Add(fromMatch.Groups["image"].Value.Trim().Trim('"'));
                    continue;
                }

                var copyMatch = Regex.Match(text, @"^(?<kind>COPY|ADD)\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
                if (!copyMatch.Success)
                {
                    continue;
                }

                var kind = copyMatch.Groups["kind"].Value.ToUpperInvariant();
                var rest = copyMatch.Groups["rest"].Value.Trim();
                var fromStage = rest.IndexOf("--from=", StringComparison.OrdinalIgnoreCase) >= 0;
                while (rest.StartsWith("--", StringComparison.Ordinal))
                {
                    var nextSpace = rest.IndexOf(' ');
                    if (nextSpace < 0)
                    {
                        rest = string.Empty;
                        break;
                    }
                    rest = rest.Substring(nextSpace + 1).TrimStart();
                }

                var sources = ExtractDockerfileCopySources(rest);
                for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
                {
                    var normalized = NormalizeDockerfileSourcePath(sources[sourceIndex]);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    var external = fromStage ||
                                   normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                   normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    result.PackageSources.Add(new ImageWizardDockerfileSource(
                        normalized,
                        kind,
                        instruction.LineNumber,
                        external,
                        normalized.IndexOf('*') >= 0 || normalized.IndexOf('?') >= 0,
                        normalized.EndsWith("/", StringComparison.Ordinal)));
                }
            }

            return result;
        }

        private static List<ImageWizardLogicalInstruction> BuildDockerfileLogicalInstructions(string text)
        {
            var result = new List<ImageWizardLogicalInstruction>();
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var current = new StringBuilder();
            var startLine = 1;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i] ?? string.Empty;
                if (current.Length == 0)
                {
                    startLine = i + 1;
                }
                current.Append(line.TrimEnd().TrimEnd('\\')).Append(' ');
                if (line.TrimEnd().EndsWith("\\", StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(new ImageWizardLogicalInstruction(startLine, current.ToString().Trim()));
                current.Clear();
            }
            if (current.Length > 0)
            {
                result.Add(new ImageWizardLogicalInstruction(startLine, current.ToString().Trim()));
            }
            return result;
        }

        private static void ApplyWizardTemplateRules(ImageCreateWizardState state)
        {
            var rules = GetImageWizardTemplateRules();
            var packageNames = new HashSet<string>(
                state.PackageBindings.Where(p => p.IsRequired).Select(p => Path.GetFileName(p.ContextPath)),
                StringComparer.OrdinalIgnoreCase);
            var activeRules = new List<ImageWizardTemplateRule>();
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                var present = rule.RequiredFiles.Where(packageNames.Contains).ToList();
                var triggerFiles = rule.RequiredFiles
                    .Where(f => !string.Equals(f, "xenomai.tar.gz", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (!triggerFiles.Any(packageNames.Contains))
                {
                    continue;
                }

                activeRules.Add(rule);
                var missing = rule.RequiredFiles.Where(f => !packageNames.Contains(f)).ToList();
                if (missing.Count > 0)
                {
                    AddWizardDiagnostic(
                        state,
                        WizardDiagnosticSeverity.Error,
                        "配套关系",
                        string.Format("{0} 已匹配 {1}，还缺少配套文件：{2}", rule.DisplayName, string.Join("、", present), string.Join("、", missing)));
                }
                else
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Info, "配套关系", rule.DisplayName + " 配套完整。");
                }

                for (var bindingIndex = 0; bindingIndex < state.PackageBindings.Count; bindingIndex++)
                {
                    var binding = state.PackageBindings[bindingIndex];
                    if (rule.RequiredFiles.Contains(Path.GetFileName(binding.ContextPath), StringComparer.OrdinalIgnoreCase))
                    {
                        binding.RelationText = rule.DisplayName;
                    }
                }
            }

            var familyRules = activeRules.Where(r => r.IsExclusiveFamily).ToList();
            if (familyRules.Count > 1)
            {
                AddWizardDiagnostic(
                    state,
                    WizardDiagnosticSeverity.Error,
                    "冲突关系",
                    "同时检测到多个互斥模板族：" + string.Join("、", familyRules.Select(r => r.DisplayName)));
            }
        }

        private static void ApplyWizardVersionRules(ImageCreateWizardState state)
        {
            for (var i = 0; i < state.PackageBindings.Count; i++)
            {
                var binding = state.PackageBindings[i];
                if (string.IsNullOrWhiteSpace(binding.SourcePath))
                {
                    continue;
                }

                var requiredVersion = ExtractWizardVersion(Path.GetFileName(binding.ContextPath));
                var actualVersion = ExtractWizardVersion(Path.GetFileName(binding.SourcePath));
                if (!string.IsNullOrWhiteSpace(requiredVersion) &&
                    !string.IsNullOrWhiteSpace(actualVersion) &&
                    !string.Equals(requiredVersion, actualVersion, StringComparison.OrdinalIgnoreCase))
                {
                    binding.VersionText = requiredVersion + " ≠ " + actualVersion;
                    AddWizardDiagnostic(
                        state,
                        WizardDiagnosticSeverity.Error,
                        "版本不匹配",
                        string.Format("{0} 需要版本 {1}，所选文件版本为 {2}。", binding.ContextPath, requiredVersion, actualVersion));
                }
            }

            var rolePackages = state.PackageBindings
                .Where(p => !string.IsNullOrWhiteSpace(p.SourcePath))
                .Select(p => new { Binding = p, Match = Regex.Match(Path.GetFileName(p.SourcePath), @"^(?<family>.+?)[_-](?:engine|lib)[_-]?(?<version>v?\d+(?:\.\d+)*)", RegexOptions.IgnoreCase) })
                .Where(x => x.Match.Success)
                .GroupBy(x => x.Match.Groups["family"].Value, StringComparer.OrdinalIgnoreCase);
            foreach (var group in rolePackages)
            {
                var versions = group.Select(x => x.Match.Groups["version"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (versions.Count > 1)
                {
                    AddWizardDiagnostic(state, WizardDiagnosticSeverity.Error, "版本不匹配", group.Key + " 的 engine/lib 版本不一致：" + string.Join("、", versions));
                }
            }
        }

        private static List<ImageWizardTemplateRule> GetImageWizardTemplateRules()
        {
            return new List<ImageWizardTemplateRule>
            {
                new ImageWizardTemplateRule("AI 运行环境", true, "ai_engine.tar.gz", "ai_lib.tar.gz", "xenomai.tar.gz"),
                new ImageWizardTemplateRule("BH 运行环境", true, "bh_engine.tar.gz", "bh_lib.tar.gz", "xenomai.tar.gz"),
                new ImageWizardTemplateRule("DB 运行环境", true, "db_engine.tar.gz", "db_lib.tar.gz", "xenomai.tar.gz"),
                new ImageWizardTemplateRule("IMG 运行环境", true, "img_engine.tar.gz", "img_lib.tar.gz", "xenomai.tar.gz"),
                new ImageWizardTemplateRule("压力测试环境", true, "stresstest_engine.tar.gz", "stresstest_lib.tar.gz", "stresstest_xenomai.tar.gz"),
                new ImageWizardTemplateRule("BH Server", false, "bond_demo_v4.tar.gz")
            };
        }

        private static string BuildImageWizardDiagnosticsText(ImageCreateWizardState state)
        {
            return string.Join(
                Environment.NewLine,
                state.Diagnostics.Select(d =>
                    (d.Severity == WizardDiagnosticSeverity.Error ? "[错误] " : d.Severity == WizardDiagnosticSeverity.Warning ? "[警告] " : "[信息] ") +
                    d.Category + "：" + d.Message));
        }

        private static string BuildImageWizardSummary(ImageCreateWizardState state, string repository, string tag, string outputDirectory)
        {
            var errors = ValidateImageWizardForBuild(state, repository, tag, outputDirectory);
            var matchedPackages = state.PackageBindings.Count(p => !string.IsNullOrWhiteSpace(p.SourcePath));
            var requiredPackages = state.PackageBindings.Count(p => p.IsRequired);
            var sb = new StringBuilder();
            sb.AppendLine("镜像：" + ((repository ?? string.Empty).Trim()) + ":" + ((tag ?? string.Empty).Trim()));
            sb.AppendLine("基础镜像：" + (state.FromImages.Count == 0 ? "（未声明）" : string.Join("、", state.FromImages)));
            sb.AppendLine(string.Format("程序包：已匹配 {0} / 必需 {1}", matchedPackages, requiredPackages));
            sb.AppendLine("输出目录：" + NormalizeLocalPath(outputDirectory));
            sb.AppendLine("独立构建目录：将在 tempBuild_image 下按时间创建");
            sb.AppendLine();
            sb.AppendLine(errors.Count == 0 ? "校验结果：通过，可以开始构建。" : "校验结果：未通过");
            for (var i = 0; i < errors.Count; i++)
            {
                sb.AppendLine("• " + errors[i]);
            }
            var warnings = state.Diagnostics.Where(d => d.Severity == WizardDiagnosticSeverity.Warning).ToList();
            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("警告：");
                for (var i = 0; i < warnings.Count; i++)
                {
                    sb.AppendLine("• " + warnings[i].Message);
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static List<string> ValidateImageWizardForBuild(ImageCreateWizardState state, string repository, string tag, string outputDirectory)
        {
            var errors = state.Diagnostics
                .Where(d => d.Severity == WizardDiagnosticSeverity.Error)
                .Select(d => d.Category + "：" + d.Message)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var repositoryValue = (repository ?? string.Empty).Trim();
            var tagValue = (tag ?? string.Empty).Trim();
            if (!Regex.IsMatch(repositoryValue, @"^[a-z0-9]+(?:(?:[._-]|/)[a-z0-9]+)*$"))
            {
                errors.Add("镜像名称格式无效；请使用小写字母、数字及 . _ - /。");
            }
            if (!Regex.IsMatch(tagValue, @"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$"))
            {
                errors.Add("Tag 格式无效。");
            }
            var normalizedOutput = NormalizeLocalPath(outputDirectory);
            if (string.IsNullOrWhiteSpace(normalizedOutput))
            {
                errors.Add("输出目录不能为空。");
            }
            else
            {
                try
                {
                    Path.GetFullPath(normalizedOutput);
                }
                catch (Exception ex)
                {
                    errors.Add("输出目录无效：" + ex.Message);
                }
            }
            if (string.IsNullOrWhiteSpace(state.DockerfileText))
            {
                errors.Add("Dockerfile 内容不能为空。");
            }
            return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ReplaceFirstFromInstruction(string dockerfileText, string newImage)
        {
            var text = dockerfileText ?? string.Empty;
            var regex = new Regex(@"^(?<prefix>\s*FROM\s+(?:(?:--\S+)\s+)*)(?<image>""[^""]+""|\S+)(?<suffix>.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var match = regex.Match(text);
            if (!match.Success)
            {
                return "FROM " + newImage + Environment.NewLine + text;
            }
            return text.Substring(0, match.Index) + match.Groups["prefix"].Value + newImage + match.Groups["suffix"].Value + text.Substring(match.Index + match.Length);
        }

        private static string AppendCopyInstructions(string dockerfileText, IEnumerable<string> filePaths)
        {
            var sb = new StringBuilder((dockerfileText ?? string.Empty).TrimEnd());
            sb.AppendLine().AppendLine().AppendLine("# 由新建镜像向导显式添加");
            foreach (var path in filePaths ?? Enumerable.Empty<string>())
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    sb.AppendLine("COPY [\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\", \"/home/" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]");
                }
            }
            return sb.ToString();
        }

        private static string RemoveDockerfileInstructionsForSource(string dockerfileText, string contextPath)
        {
            var target = NormalizeWizardContextPath(contextPath);
            var lines = (dockerfileText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var kept = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var match = Regex.Match(StripDockerfileComment(line).Trim(), @"^(?:COPY|ADD)\s+(.+)$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var rest = match.Groups[1].Value.Trim();
                    while (rest.StartsWith("--", StringComparison.Ordinal))
                    {
                        var space = rest.IndexOf(' ');
                        rest = space < 0 ? string.Empty : rest.Substring(space + 1).TrimStart();
                    }
                    var sources = ExtractDockerfileCopySources(rest).Select(NormalizeWizardContextPath).ToList();
                    if (sources.Contains(target, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                kept.Add(line);
            }
            return string.Join(Environment.NewLine, kept);
        }

        private static string NormalizeWizardContextPath(string path)
        {
            return NormalizeDockerfileSourcePath(path ?? string.Empty).Trim();
        }

        private static string ResolveImportedDockerfileSource(string importedDirectory, string contextPath)
        {
            if (string.IsNullOrWhiteSpace(importedDirectory) || !Directory.Exists(importedDirectory) || string.IsNullOrWhiteSpace(contextPath))
            {
                return string.Empty;
            }
            try
            {
                var root = Path.GetFullPath(importedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(Path.Combine(importedDirectory, contextPath.Replace('/', Path.DirectorySeparatorChar)));
                return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool WildcardMatches(string pattern, string value)
        {
            var regex = "^" + Regex.Escape(pattern ?? string.Empty).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value ?? string.Empty, regex, RegexOptions.IgnoreCase);
        }

        private static string GetDockerRepositoryPart(string repoTag)
        {
            var value = NormalizeRepoTag(repoTag);
            var slash = value.LastIndexOf('/');
            var colon = value.LastIndexOf(':');
            return colon > slash ? value.Substring(0, colon) : value;
        }

        private static string ExtractWizardVersion(string fileName)
        {
            var match = Regex.Match(fileName ?? string.Empty, @"(?<![A-Za-z0-9])v?(?<version>\d+(?:\.\d+){1,3})(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["version"].Value : string.Empty;
        }

        private static void ApplyWizardPackageVersion(ImageWizardPackageBinding binding)
        {
            if (binding == null)
            {
                return;
            }
            var version = ExtractWizardVersion(Path.GetFileName(binding.SourcePath));
            binding.VersionText = string.IsNullOrWhiteSpace(version) ? "-" : version;
        }

        private static void AddWizardDiagnostic(ImageCreateWizardState state, WizardDiagnosticSeverity severity, string category, string message)
        {
            if (state.Diagnostics.Any(d => d.Severity == severity && string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Message, message, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            state.Diagnostics.Add(new ImageWizardDiagnostic(severity, category, message));
        }

        private static ImageWizardPackageBinding CloneWizardPackageBinding(ImageWizardPackageBinding source)
        {
            return new ImageWizardPackageBinding(source.ContextPath, source.SourcePath, source.IsRequired, source.Instruction, source.LineNumber)
            {
                RelationText = source.RelationText,
                VersionText = source.VersionText
            };
        }

        private async Task BuildImageFromWizardAsync(ImageCreateWizardRequest request)
        {
            var cancellation = new CancellationTokenSource();
            var isRunning = true;
            var closeCompletion = new TaskCompletionSource<bool>();
            var logBuffer = new StringBuilder();

            var progressRoot = new Grid { Margin = new Thickness(16) };
            progressRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            progressRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            progressRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            progressRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var stageText = new TextBlock
            {
                Text = "阶段：准备构建",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            progressRoot.Children.Add(stageText);
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 17, Value = 0, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(progressBar, 1);
            progressRoot.Children.Add(progressBar);
            var logBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240))
            };
            Grid.SetRow(logBox, 2);
            progressRoot.Children.Add(logBox);
            var cancelButton = new Button
            {
                Content = "取消构建",
                Width = 110,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(cancelButton, 3);
            progressRoot.Children.Add(cancelButton);

            var progressWindow = new Window
            {
                Title = "正在新建镜像",
                Width = 920,
                Height = 620,
                MinWidth = 760,
                MinHeight = 480,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = progressRoot
            };
            EnsureWindowCenteredOnOwner(progressWindow);

            Action<string> appendLog = line =>
            {
                var value = line ?? string.Empty;
                lock (logBuffer)
                {
                    logBuffer.AppendLine(value);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    logBox.AppendText(value + Environment.NewLine);
                    logBox.ScrollToEnd();
                }));
            };
            Action<string, double, bool> reportStage = (stage, percent, indeterminate) => Dispatcher.BeginInvoke(new Action(() =>
            {
                stageText.Text = "阶段：" + stage;
                progressBar.IsIndeterminate = indeterminate;
                if (!indeterminate)
                {
                    progressBar.Value = Math.Max(0, Math.Min(100, percent));
                }
            }));

            cancelButton.Click += (_, __) =>
            {
                if (!isRunning)
                {
                    progressWindow.Close();
                    return;
                }
                cancelButton.IsEnabled = false;
                cancelButton.Content = "正在取消...";
                appendLog("[向导] 用户请求取消，正在终止当前 Docker 命令...");
                cancellation.Cancel();
            };
            progressWindow.Closing += (_, e) =>
            {
                if (isRunning)
                {
                    cancellation.Cancel();
                    e.Cancel = true;
                    cancelButton.IsEnabled = false;
                    cancelButton.Content = "正在取消...";
                }
            };
            progressWindow.Closed += (_, __) => closeCompletion.TrySetResult(true);
            progressWindow.Show();

            var buildResult = await PerformWizardImageBuildAsync(request, cancellation.Token, appendLog, reportStage);
            isRunning = false;
            cancelButton.IsEnabled = true;
            cancelButton.Content = "关闭";
            if (buildResult.Success)
            {
                reportStage("完成", 100, false);
                stageText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                appendLog("[完成] 镜像构建、导出和列表刷新均已完成。");
            }
            else if (buildResult.Canceled)
            {
                reportStage("已取消", progressBar.Value, false);
                stageText.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                appendLog("[取消] 构建已取消；已保留独立构建目录用于排查。");
            }
            else
            {
                reportStage("失败", progressBar.Value, false);
                stageText.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                appendLog("[失败] " + buildResult.ErrorMessage);
            }

            var logPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(buildResult.BuildDirectory) && Directory.Exists(buildResult.BuildDirectory))
            {
                try
                {
                    logPath = Path.Combine(buildResult.BuildDirectory, "build.log");
                    string snapshot;
                    lock (logBuffer)
                    {
                        snapshot = logBuffer.ToString();
                    }
                    File.WriteAllText(logPath, snapshot, new UTF8Encoding(false));
                }
                catch
                {
                    logPath = string.Empty;
                }
            }

            await closeCompletion.Task;
            if (buildResult.Success)
            {
                MessageBox.Show(
                    this,
                    string.Format(
                        "新建镜像成功：\n镜像：{0}\n独立构建目录：{1}\nDockerfile：{2}\n镜像实体文件：{3}{4}",
                        request.ImageReference,
                        buildResult.BuildDirectory,
                        Path.Combine(buildResult.BuildDirectory, "Dockerfile"),
                        buildResult.ArchivePath,
                        string.IsNullOrWhiteSpace(logPath) ? string.Empty : "\n构建日志：" + logPath),
                    "镜像编排",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (!buildResult.Canceled)
            {
                ShowScrollableErrorDialog(
                    "新建镜像失败。独立构建目录已保留。",
                    buildResult.ErrorMessage +
                    (string.IsNullOrWhiteSpace(buildResult.BuildDirectory) ? string.Empty : "\n\n构建目录：" + buildResult.BuildDirectory) +
                    (string.IsNullOrWhiteSpace(logPath) ? string.Empty : "\n构建日志：" + logPath));
            }
        }

        private async Task<ImageWizardBuildResult> PerformWizardImageBuildAsync(
            ImageCreateWizardRequest request,
            CancellationToken cancellationToken,
            Action<string> appendLog,
            Action<string, double, bool> reportStage)
        {
            var buildRoot = GetTempBuildImageDirectory();
            var buildDirectory = Path.Combine(buildRoot, "build_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            var archivePath = string.Empty;
            try
            {
                reportStage("创建独立构建目录", 5, false);
                appendLog("[准备] 镜像：" + request.ImageReference);
                appendLog("[准备] 构建目录：" + buildDirectory);
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(buildDirectory);
                    StageWizardPackages(buildDirectory, request.PackageBindings, cancellationToken, appendLog);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.WriteAllText(Path.Combine(buildDirectory, "Dockerfile"), request.DockerfileText, new UTF8Encoding(false));
                }, cancellationToken);

                reportStage("构建镜像（实时日志）", 25, true);
                appendLog("[命令] docker build --progress=plain -t \"" + request.ImageReference + "\" \"" + buildDirectory + "\"");
                var buildCommand = string.Format("build --progress=plain -t \"{0}\" \"{1}\"", request.ImageReference, buildDirectory);
                var buildRun = await RunWizardDockerCommandAsync(buildCommand, 3600000, cancellationToken, appendLog);
                if (buildRun.Canceled)
                {
                    return ImageWizardBuildResult.CanceledResult(buildDirectory);
                }
                if (!buildRun.Success)
                {
                    return ImageWizardBuildResult.Failed(buildDirectory, "docker build 失败：\n" + buildRun.Output);
                }

                reportStage("导出镜像 tar（实时日志）", 80, true);
                Directory.CreateDirectory(request.OutputDirectory);
                archivePath = BuildUniqueWizardArchivePath(request.OutputDirectory, request.ImageReference);
                appendLog("[命令] docker image save \"" + request.ImageReference + "\" -o \"" + archivePath + "\"");
                var saveCommand = string.Format("image save \"{0}\" -o \"{1}\"", request.ImageReference, archivePath);
                var saveRun = await RunWizardDockerCommandAsync(saveCommand, 3600000, cancellationToken, appendLog);
                if (saveRun.Canceled)
                {
                    TryDeleteFileQuietly(archivePath);
                    return ImageWizardBuildResult.CanceledResult(buildDirectory);
                }
                if (!saveRun.Success || !File.Exists(archivePath))
                {
                    TryDeleteFileQuietly(archivePath);
                    return ImageWizardBuildResult.Failed(buildDirectory, "镜像已构建，但导出失败：\n" + saveRun.Output);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(archivePath);
                if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    return ImageWizardBuildResult.Failed(buildDirectory, "导出的镜像文件是链接文件，请更换输出目录后重试。");
                }

                var inspectResult = await RunWizardDockerCommandAsync(
                    string.Format("image inspect --format \"{{{{.Id}}}}\" \"{0}\"", request.ImageReference),
                    30000,
                    cancellationToken,
                    appendLog);
                if (inspectResult.Canceled)
                {
                    throw new OperationCanceledException();
                }
                var exportedImageId = NormalizeDeployImageId(inspectResult.Output);
                WriteDeployArchiveMetadata(archivePath, request.ImageReference, exportedImageId);
                appendLog(string.IsNullOrWhiteSpace(exportedImageId)
                    ? "[警告] 镜像已导出，但未读取到镜像 ID；本次 tar 不会用于快速部署缓存。"
                    : "[缓存] 已登记镜像包，后续部署可直接复用。镜像 ID：" + exportedImageId);
                reportStage("刷新镜像列表", 94, false);
                appendLog("[刷新] 正在读取本机 Docker 镜像并更新页面...");
                _promotedPreparedImageRepoTags.Add(NormalizeRepoTag(request.ImageReference));
                SavePromotedPreparedImageStore();
                RefreshLocalServiceImageLists();
                ShowServiceImagePanel();
                SetSidebarSelected("ServiceImage");
                BindServiceImageRows(GetSelectedServiceImageDeviceName());
                ClearCreateSelectionsAfterSuccess();
                return ImageWizardBuildResult.Succeeded(buildDirectory, archivePath);
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrWhiteSpace(archivePath))
                {
                    TryDeleteFileQuietly(archivePath);
                }
                return ImageWizardBuildResult.CanceledResult(buildDirectory);
            }
            catch (Exception ex)
            {
                return ImageWizardBuildResult.Failed(buildDirectory, ex.Message);
            }
        }

        private static void StageWizardPackages(
            string buildDirectory,
            List<ImageWizardPackageBinding> packageBindings,
            CancellationToken cancellationToken,
            Action<string> appendLog)
        {
            var copiedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < packageBindings.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var binding = packageBindings[i];
                if (binding == null || string.IsNullOrWhiteSpace(binding.SourcePath))
                {
                    continue;
                }
                if (!File.Exists(binding.SourcePath))
                {
                    throw new FileNotFoundException("程序包文件不存在。", binding.SourcePath);
                }

                var relativePath = NormalizeWizardContextPath(binding.ContextPath).Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                {
                    throw new InvalidOperationException("Dockerfile 构建上下文路径无效：" + binding.ContextPath);
                }
                var targetPath = Path.GetFullPath(Path.Combine(buildDirectory, relativePath));
                var normalizedRoot = Path.GetFullPath(buildDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!targetPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Dockerfile 构建上下文路径越界：" + binding.ContextPath);
                }

                string existingSource;
                if (copiedTargets.TryGetValue(targetPath, out existingSource))
                {
                    if (!string.Equals(existingSource, binding.SourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("多个文件映射到同一个构建路径：" + binding.ContextPath);
                    }
                    continue;
                }

                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }
                appendLog("[复制] " + binding.SourcePath + " -> " + binding.ContextPath);
                CopyFileWithCancellation(binding.SourcePath, targetPath, cancellationToken);
                copiedTargets[targetPath] = binding.SourcePath;
            }
        }

        private static void CopyFileWithCancellation(string sourcePath, string targetPath, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 1024];
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan))
            using (var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan))
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                }
            }
        }

        private static string BuildUniqueWizardArchivePath(string outputDirectory, string imageReference)
        {
            var baseName = Path.GetFileNameWithoutExtension(BuildImageArchiveFileName(imageReference));
            var path = Path.Combine(outputDirectory, baseName + ".tar");
            if (!File.Exists(path))
            {
                return path;
            }
            return Path.Combine(outputDirectory, baseName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".tar");
        }

        private static async Task<ImageWizardDockerRunResult> RunWizardDockerCommandAsync(
            string commandArguments,
            int timeoutMilliseconds,
            CancellationToken cancellationToken,
            Action<string> onLine)
        {
            return await Task.Run(() =>
            {
                var buffer = new StringBuilder();
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = commandArguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                try
                {
                    using (var process = new Process())
                    {
                        process.StartInfo = psi;
                        process.OutputDataReceived += (_, e) => AppendWizardProcessLine(e.Data, buffer, onLine);
                        process.ErrorDataReceived += (_, e) => AppendWizardProcessLine(e.Data, buffer, onLine);
                        if (!process.Start())
                        {
                            return ImageWizardDockerRunResult.Failed("无法启动 docker 进程。");
                        }
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        var started = DateTime.UtcNow;
                        while (!process.WaitForExit(200))
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                TryKillWizardProcess(process);
                                return ImageWizardDockerRunResult.CanceledResult(buffer.ToString());
                            }
                            if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMilliseconds)
                            {
                                TryKillWizardProcess(process);
                                return ImageWizardDockerRunResult.Failed("命令执行超时。\n" + buffer);
                            }
                        }
                        process.WaitForExit();
                        var output = buffer.ToString().Trim();
                        return process.ExitCode == 0
                            ? ImageWizardDockerRunResult.Succeeded(output)
                            : ImageWizardDockerRunResult.Failed(output);
                    }
                }
                catch (Exception ex)
                {
                    return ImageWizardDockerRunResult.Failed(ex.Message);
                }
            });
        }

        private static void AppendWizardProcessLine(string line, StringBuilder buffer, Action<string> onLine)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            lock (buffer)
            {
                buffer.AppendLine(line);
            }
            if (onLine != null)
            {
                onLine(line);
            }
        }

        private static void TryKillWizardProcess(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch
            {
            }
        }

        private sealed class ImageCreateWizardState
        {
            public ImageCreateWizardState()
            {
                ExtraPackagePaths = new List<string>();
                ManualPackageMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                DisabledAutoMatchContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                LocalImageRepoTags = new List<string>();
                FromImages = new List<string>();
                PackageBindings = new List<ImageWizardPackageBinding>();
                Diagnostics = new List<ImageWizardDiagnostic>();
            }

            public string DockerfileText { get; set; }
            public string SourceDescription { get; set; }
            public string LastLoadedSourceText { get; set; }
            public string ImportedDockerfileDirectory { get; set; }
            public List<string> ExtraPackagePaths { get; }
            public Dictionary<string, string> ManualPackageMappings { get; }
            public HashSet<string> DisabledAutoMatchContexts { get; }
            public List<string> LocalImageRepoTags { get; }
            public List<string> FromImages { get; }
            public List<ImageWizardPackageBinding> PackageBindings { get; }
            public List<ImageWizardDiagnostic> Diagnostics { get; }
        }

        private sealed class ImageCreateWizardRequest
        {
            public ImageCreateWizardRequest(
                string dockerfileText,
                string repository,
                string tag,
                string outputDirectory,
                List<ImageWizardPackageBinding> packageBindings,
                List<ImageWizardDiagnostic> diagnostics)
            {
                DockerfileText = dockerfileText ?? string.Empty;
                Repository = repository ?? string.Empty;
                Tag = tag ?? string.Empty;
                OutputDirectory = outputDirectory ?? string.Empty;
                PackageBindings = packageBindings ?? new List<ImageWizardPackageBinding>();
                Diagnostics = diagnostics ?? new List<ImageWizardDiagnostic>();
            }

            public string DockerfileText { get; }
            public string Repository { get; }
            public string Tag { get; }
            public string ImageReference => Repository + ":" + Tag;
            public string OutputDirectory { get; }
            public List<ImageWizardPackageBinding> PackageBindings { get; }
            public List<ImageWizardDiagnostic> Diagnostics { get; }
        }

        private sealed class ImageWizardTemplateChoice
        {
            public ImageWizardTemplateChoice(string displayName, string content, string description)
            {
                DisplayName = displayName ?? string.Empty;
                Content = content ?? string.Empty;
                Description = description ?? string.Empty;
            }
            public string DisplayName { get; }
            public string Content { get; }
            public string Description { get; }
        }

        private sealed class ImageWizardPackageBinding
        {
            public ImageWizardPackageBinding(string contextPath, string sourcePath, bool isRequired, string instruction, int lineNumber)
            {
                ContextPath = contextPath ?? string.Empty;
                SourcePath = sourcePath ?? string.Empty;
                IsRequired = isRequired;
                Instruction = instruction ?? string.Empty;
                LineNumber = lineNumber;
                RelationText = isRequired ? "Dockerfile 必需" : "多余/附加";
                VersionText = "-";
            }
            public string ContextPath { get; set; }
            public string SourcePath { get; set; }
            public bool IsRequired { get; }
            public string Instruction { get; }
            public int LineNumber { get; }
            public string RelationText { get; set; }
            public string VersionText { get; set; }
            public string StatusText => string.IsNullOrWhiteSpace(SourcePath) ? "缺失" : (IsRequired ? "已匹配" : "多余");
            public string LocalFileName => string.IsNullOrWhiteSpace(SourcePath) ? "（未选择）" : Path.GetFileName(SourcePath);
        }

        private sealed class ImageWizardDiagnostic
        {
            public ImageWizardDiagnostic(WizardDiagnosticSeverity severity, string category, string message)
            {
                Severity = severity;
                Category = category ?? string.Empty;
                Message = message ?? string.Empty;
            }
            public WizardDiagnosticSeverity Severity { get; }
            public string Category { get; }
            public string Message { get; }
        }

        private enum WizardDiagnosticSeverity
        {
            Info,
            Warning,
            Error
        }

        private sealed class ImageWizardDockerfileReferences
        {
            public ImageWizardDockerfileReferences()
            {
                FromImages = new List<string>();
                PackageSources = new List<ImageWizardDockerfileSource>();
            }
            public List<string> FromImages { get; }
            public List<ImageWizardDockerfileSource> PackageSources { get; }
        }

        private sealed class ImageWizardDockerfileSource
        {
            public ImageWizardDockerfileSource(string contextPath, string instruction, int lineNumber, bool isExternalOrStage, bool hasWildcard, bool isDirectory)
            {
                ContextPath = contextPath ?? string.Empty;
                Instruction = instruction ?? string.Empty;
                LineNumber = lineNumber;
                IsExternalOrStage = isExternalOrStage;
                HasWildcard = hasWildcard;
                IsDirectory = isDirectory;
            }
            public string ContextPath { get; }
            public string Instruction { get; }
            public int LineNumber { get; }
            public bool IsExternalOrStage { get; }
            public bool HasWildcard { get; }
            public bool IsDirectory { get; }
        }

        private sealed class ImageWizardLogicalInstruction
        {
            public ImageWizardLogicalInstruction(int lineNumber, string text)
            {
                LineNumber = lineNumber;
                Text = text ?? string.Empty;
            }
            public int LineNumber { get; }
            public string Text { get; }
        }

        private sealed class ImageWizardTemplateRule
        {
            public ImageWizardTemplateRule(string displayName, bool isExclusiveFamily, params string[] requiredFiles)
            {
                DisplayName = displayName ?? string.Empty;
                IsExclusiveFamily = isExclusiveFamily;
                RequiredFiles = (requiredFiles ?? new string[0]).ToList();
            }
            public string DisplayName { get; }
            public bool IsExclusiveFamily { get; }
            public List<string> RequiredFiles { get; }
        }

        private sealed class ImageWizardBuildResult
        {
            private ImageWizardBuildResult(bool success, bool canceled, string buildDirectory, string archivePath, string errorMessage)
            {
                Success = success;
                Canceled = canceled;
                BuildDirectory = buildDirectory ?? string.Empty;
                ArchivePath = archivePath ?? string.Empty;
                ErrorMessage = errorMessage ?? string.Empty;
            }
            public bool Success { get; }
            public bool Canceled { get; }
            public string BuildDirectory { get; }
            public string ArchivePath { get; }
            public string ErrorMessage { get; }
            public static ImageWizardBuildResult Succeeded(string buildDirectory, string archivePath) { return new ImageWizardBuildResult(true, false, buildDirectory, archivePath, string.Empty); }
            public static ImageWizardBuildResult Failed(string buildDirectory, string message) { return new ImageWizardBuildResult(false, false, buildDirectory, string.Empty, message); }
            public static ImageWizardBuildResult CanceledResult(string buildDirectory) { return new ImageWizardBuildResult(false, true, buildDirectory, string.Empty, string.Empty); }
        }

        private sealed class ImageWizardDockerRunResult
        {
            private ImageWizardDockerRunResult(bool success, bool canceled, string output)
            {
                Success = success;
                Canceled = canceled;
                Output = output ?? string.Empty;
            }
            public bool Success { get; }
            public bool Canceled { get; }
            public string Output { get; }
            public static ImageWizardDockerRunResult Succeeded(string output) { return new ImageWizardDockerRunResult(true, false, output); }
            public static ImageWizardDockerRunResult Failed(string output) { return new ImageWizardDockerRunResult(false, false, output); }
            public static ImageWizardDockerRunResult CanceledResult(string output) { return new ImageWizardDockerRunResult(false, true, output); }
        }
    }
}
