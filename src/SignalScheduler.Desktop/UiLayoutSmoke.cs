using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
            home.GroupsGrid.ItemsSource=new[]
            {
                new BatchGroupRow(new ManagedGroup("g-1",
                    "德语业务沟通及长名称演示群组",3,true)),
                new BatchGroupRow(new ManagedGroup("g-2",
                    "另一个示例群组",2,false))
            };
            CheckPage(home,"首页群组选择",home.GroupsGrid,
                new[]{50d,150d,60d},report);

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
        double[] minWidths,StringBuilder report)
    {
        var window=new Window
        {
            Title="MS002 UI smoke",
            Width=1220,
            Height=810,
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
