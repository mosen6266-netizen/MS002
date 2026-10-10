using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

/// <summary>
/// Windows-only build gate: render real WPF pages with representative local
/// rows and reject accidentally collapsed DataGrid columns. Does not start
/// the Engine, use credentials, or send Signal messages.
/// </summary>
internal static class UiLayoutSmoke
{
    public static int Run(string path)
    {
        var report=new StringBuilder("MS002 WPF UI width/layout smoke test\n");
        Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        try
        {
            var home=new LiveBatchWindow();
            home.GroupsGrid.ItemsSource=Enumerable.Range(1,10)
                .Select(i=>new BatchGroupRow(new ManagedGroup(
                    "g-"+i,"群组名称示例 "+i,2,false))).ToArray();
            CheckPage(home,"首页群组选择",home.GroupsGrid,
                new[]{50d,150d,60d},report,windowHeight:620);

            // Ensure the group title is not covered by select/clear buttons
            // even at the compact 620px viewport used for the home dashboard.
            var link=new LinkAccountWindow();
            link.FinishButton.ApplyTemplate();
            report.AppendLine("扫码禁用按钮实际颜色："+
                $"背景={link.FinishButton.Background}，文字={link.FinishButton.Foreground}，"+
                $"IsEnabled={link.FinishButton.IsEnabled}");
            if(link.FinishButton.IsEnabled ||
               link.FinishButton.Background is not SolidColorBrush disabledBg ||
               disabledBg.Color!=Color.FromRgb(41,46,54) ||
               link.FinishButton.Foreground is not SolidColorBrush disabledFg ||
               disabledFg.Color!=Color.FromRgb(246,247,250))
                throw new InvalidOperationException(
                    "扫码备注完成按钮的禁用状态不是深灰底、浅色文字。");
            report.AppendLine("扫码备注按钮禁用状态：PASS");

            var monitor=new RunningTasksWindow();
            if(monitor.MonitorGrid.Columns.ElementAtOrDefault(2) is not DataGridTemplateColumn ||
               monitor.MonitorGrid.Columns[2].Header?.ToString()!="运行状态")
                throw new InvalidOperationException(
                    "当前运行任务缺少红绿高对比状态徽标模板。");
            report.AppendLine("任务状态徽标列：PASS");

            var account=new AccountGroupWindow();
            account.AccountGrid.ItemsSource=new[]
            {
                new ManagedAccount("+10000000001","账号备注示例",true,true,1),
                new ManagedAccount("+10000000002","另一账号",true,false,1)
            };
            CheckPage(account,"账号管理",account.AccountGrid,
                new[]{110d,95d,60d,60d},report);

            var groups=new AccountGroupWindow();
            groups.ManagementTabs.SelectedIndex=1;
            groups.GroupGrid.ItemsSource=new[]
            {
                new GroupSelectionRow(new ManagedGroup("g-1","名称较长的群组示例",2,true)),
                new GroupSelectionRow(new ManagedGroup("g-2","其他群组",1,false))
            };
            CheckPage(groups,"群组管理",groups.GroupGrid,
                new[]{60d,150d,80d},report);

            var scripts=new ScriptEditorWindow();
            scripts.StepsGrid.ItemsSource=new[]
            {
                new ScriptStepRow(new ScriptEditorStep(
                    0,"","第一条示例消息，测试内容列宽和操作按钮", "",false,"",5,5)),
                new ScriptStepRow(new ScriptEditorStep(
                    1,"","第二条示例消息", "",false,"",5,5))
            };
            // The editor fits its essential columns without horizontal scrolling.
            CheckPage(scripts,"剧本消息表格",scripts.StepsGrid,
                new[]{45d,105d,130d,170d},report);

            // The settings/update route must load in the same fixed viewport
            // without making network calls or requiring a GitHub login.
            var updates=new UpdateCenterWindow();
            var updateWindow=new Window
            {
                Width=1220,Height=810,Left=-4500,Top=-4500,
                WindowStartupLocation=WindowStartupLocation.Manual,
                ShowInTaskbar=false,ShowActivated=false,
                WindowStyle=WindowStyle.ToolWindow,Content=updates
            };
            try
            {
                updateWindow.Show();
                updates.UpdateLayout();
                if(updates.CheckButton.ActualWidth<70 ||
                   !updates.CurrentVersionText.Text.StartsWith("V8",StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "更新页面当前版本或检查按钮无法正常显示。");
                // The two buttons from the user's screenshot were unreadable
                // because Windows drew white native disabled faces with pale
                // disabled system text. Verify the ACTUAL WPF template values.
                VerifyReadableButton(updates.DownloadButton,"下载更新（禁用）",false,report);
                VerifyReadableButton(updates.OpenFolderButton,"查看已下载文件（禁用）",false,report);
                VerifyReadableButton(updates.CheckButton,"检查更新（可用）",true,report);
                report.AppendLine("GitHub 检查更新页面：PASS");
            }
            finally{updateWindow.Close();}

            // The image action must be visually distinguishable on every
            // message row, including when the user moves the pointer.
            var add=new Button
            {
                Style=(Style)scripts.FindResource("ImageRowButton"),
                DataContext=new ScriptStepRow(),
                Content="添加图片"
            };
            var view=new Button
            {
                Style=(Style)scripts.FindResource("ImageRowButton"),
                DataContext=new ScriptStepRow(new ScriptEditorStep(
                    0,"","", "img:test",false,"",5,5)),
                Content="预览图片"
            };
            var colorHost=new Window
            {
                Width=360,Height=130,Left=-4500,Top=-4500,
                WindowStartupLocation=WindowStartupLocation.Manual,
                ShowInTaskbar=false,ShowActivated=false,
                WindowStyle=WindowStyle.ToolWindow,
                Content=new StackPanel{Children={add,view}}
            };
            try
            {
                colorHost.Show();
                colorHost.UpdateLayout();
                if(add.Background?.ToString()==view.Background?.ToString())
                    throw new InvalidOperationException(
                        "添加图片和预览图片未形成不同颜色。");
                report.AppendLine("逐条图片状态颜色：PASS");
            }
            finally{colorHost.Close();}

            report.AppendLine("PASS: all measured columns exceed their minimum widths.");
            File.WriteAllText(path,report.ToString());
            return 0;
        }
        catch(Exception ex)
        {
            report.AppendLine("FAIL: "+ex);
            File.WriteAllText(path,report.ToString());
            return 1;
        }
    }

    static void VerifyReadableButton(Button button,string description,
        bool enabled,StringBuilder report)
    {
        if(button.IsEnabled!=enabled)
            throw new InvalidOperationException(description+": 按钮禁用/启用状态不符合预期。");
        if(!button.ApplyTemplate())
            throw new InvalidOperationException(description+": WPF 按钮模板没有加载。");
        var face=button.Template.FindName("ReadableButtonFace",button) as Border;
        var label=button.Template.FindName("ReadableButtonLabel",button) as ContentPresenter;
        if(face is null || label is null)
            throw new InvalidOperationException(description+": 仍使用不受控的系统原生按钮模板。");
        if(face.Background is not SolidColorBrush background ||
           label.GetValue(System.Windows.Documents.TextElement.ForegroundProperty)
               is not SolidColorBrush foreground)
            throw new InvalidOperationException(description+": 无法检查按钮背景与文字颜色。");
        var ratio=ContrastRatio(background.Color,foreground.Color);
        if(ratio<4.5)
            throw new InvalidOperationException(
                $"{description}: 对比度 {ratio:F2}:1，不符合4.5:1可读性要求。");
        if(!enabled && background.Color!=Color.FromRgb(51,60,71))
            throw new InvalidOperationException(description+": 禁用按钮没有变为深灰色。");
        report.AppendLine($"{description}：背景={background.Color}，文字={foreground.Color}，"+
            $"对比度={ratio:F2}:1 PASS");
    }

    static double ContrastRatio(Color left,Color right)
    {
        static double Lum(Color c)
        {
            static double Linear(byte b)
            {
                var v=b/255d;
                return v<=0.04045?v/12.92:Math.Pow((v+0.055)/1.055,2.4);
            }
            return 0.2126*Linear(c.R)+0.7152*Linear(c.G)+0.0722*Linear(c.B);
        }
        var a=Lum(left);
        var b=Lum(right);
        return (Math.Max(a,b)+0.05)/(Math.Min(a,b)+0.05);
    }

    static IEnumerable<T> VisualChildren<T>(DependencyObject root)
        where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T match)yield return match;
            foreach(var nested in VisualChildren<T>(child))yield return nested;
        }
    }

    static void CheckPage(UserControl page,string name,DataGrid grid,
        double[] minWidths,StringBuilder report,double windowHeight=810)
    {
        var window=new Window
        {
            Title="MS002 UI smoke",
            Width=1220,
            Height=windowHeight,
            Left=-4500,
            Top=-4500,
            WindowStartupLocation=WindowStartupLocation.Manual,
            ShowInTaskbar=false,
            ShowActivated=false,
            WindowStyle=WindowStyle.ToolWindow,
            Content=page
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            // Run the Loaded/Render queue on the same STA dispatcher.
            page.Dispatcher.Invoke(()=>{},DispatcherPriority.Loaded);
            page.UpdateLayout();
            grid.UpdateLayout();
            var widths=grid.Columns.Select(c=>c.ActualWidth).ToArray();
            report.AppendLine($"{name}: grid={grid.ActualWidth:F0}, columns="+
                string.Join(",",widths.Select(w=>w.ToString("F0"))));
            if(grid.ActualWidth<500)
                throw new InvalidOperationException($"{name}: grid surface collapsed.");
            if(name=="首页群组选择")
            {
                if(grid.Parent is not Grid groupPanel ||
                   groupPanel.Children.OfType<Grid>().FirstOrDefault(x=>Grid.GetRow(x)==0)
                       is not Grid header ||
                   header.Children.OfType<TextBlock>().FirstOrDefault() is not TextBlock title ||
                   title.ActualHeight<18 ||
                   title.ActualWidth<180)
                    throw new InvalidOperationException(
                        "首页选择群组标题被覆盖或被裁剪，未完整显示。");
                if(header.Children.OfType<StackPanel>().FirstOrDefault() is not StackPanel buttons ||
                   Grid.GetRow(buttons)!=1)
                    throw new InvalidOperationException(
                        "首页群组标题与操作按钮未使用上下两行布局。");
                if(grid.ColumnHeaderHeight<42)
                    throw new InvalidOperationException(
                        $"群组表头高度 {grid.ColumnHeaderHeight:F0} 过小，中文标签可能被裁剪。");
                var columnHeaders=VisualChildren<DataGridColumnHeader>(grid)
                    .Where(h=>h.Content is string)
                    .ToArray();
                if(columnHeaders.Length<3)
                    throw new InvalidOperationException(
                        "群组表头未完整渲染，无法确认三个标签是否可见。");
                foreach(var columnHeader in columnHeaders)
                {
                    if(columnHeader.ActualHeight<40)
                        throw new InvalidOperationException(
                            $"群组表头「{columnHeader.Content}」实际高度不足：" +
                            $"{columnHeader.ActualHeight:F0}px。");
                    if(columnHeader.Foreground is not SolidColorBrush fg ||
                       fg.Color!=Color.FromRgb(244,249,255))
                        throw new InvalidOperationException(
                            $"群组表头「{columnHeader.Content}」文字颜色不清晰。");
                    report.AppendLine($"群组表头：{columnHeader.Content} " +
                        $"高度={columnHeader.ActualHeight:F0}px PASS");
                }
                var required=10*grid.RowHeight+grid.ColumnHeaderHeight;
                if(grid.ActualHeight<required-1)
                    throw new InvalidOperationException(
                        $"首页每页十行不可完整显示：{grid.ActualHeight:F0} < {required:F0}");
            }
            for(var i=0;i<minWidths.Length;i++)
            {
                if(i>=widths.Length || widths[i]<minWidths[i])
                    throw new InvalidOperationException(
                        $"{name}: column {i+1} collapsed ({(i<widths.Length?widths[i]:0):F0}px).");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
