using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
