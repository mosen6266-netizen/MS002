using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace SignalScheduler.Desktop;

/// <summary>
/// Uniform graphite title bar for secondary application windows. Wraps
/// existing WPF content without changing any named controls, data contexts,
/// event handlers, task scheduling or account state.
/// </summary>
internal static class Ui2WindowChrome
{
    public static void Attach(Window window)
    {
        if(window.Content is not UIElement page)
            throw new InvalidOperationException("Secondary window has no WPF page.");
        window.Content=null;
        window.WindowStyle=WindowStyle.None;
        window.Background=Brush("Ui2Canvas","#101114");
        window.UseLayoutRounding=true;
        window.SnapsToDevicePixels=true;
        window.FontFamily=new FontFamily("Segoe UI");
        WindowChrome.SetWindowChrome(window,new WindowChrome{
            CaptionHeight=0,
            GlassFrameThickness=new Thickness(0),
            ResizeBorderThickness=new Thickness(window.ResizeMode==ResizeMode.NoResize?0:7),
            CornerRadius=new CornerRadius(0),
            UseAeroCaptionButtons=false
        });

        var container=new Grid();
        container.RowDefinitions.Add(new RowDefinition{Height=new GridLength(48)});
        container.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        var titleBar=new Border{
            Background=Brush("Ui2Sidebar","#141518"),
            BorderBrush=Brush("Ui2Border","#383C42"),
            BorderThickness=new Thickness(0,0,0,1)
        };
        var titleGrid=new Grid{Background=Brush("Ui2Sidebar","#141518")};
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(46)});
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(46)});
        var title=new TextBlock{
            Text=window.Title,FontWeight=FontWeights.SemiBold,
            FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis,
            Foreground=Brush("Ui2Text","#F3F3F4"),
            Margin=new Thickness(18,0,8,0),
            VerticalAlignment=VerticalAlignment.Center
        };
        titleGrid.Children.Add(title);
        var minimum=MakeButton("–","最小化窗口",()=>{
            window.WindowState=WindowState.Minimized;
        });
        Grid.SetColumn(minimum,1);
        titleGrid.Children.Add(minimum);
        if(window.ResizeMode==ResizeMode.NoResize)
            minimum.Visibility=Visibility.Collapsed;
        var close=MakeButton("×","关闭窗口",window.Close);
        Grid.SetColumn(close,2);
        titleGrid.Children.Add(close);
        titleBar.Child=titleGrid;
        Grid.SetRow(titleBar,0);
        container.Children.Add(titleBar);
        titleGrid.MouseLeftButtonDown+=(_,e)=>{
            if(e.OriginalSource is Button || e.Handled)return;
            if(e.ClickCount==2 && window.ResizeMode==ResizeMode.CanResize){
                window.WindowState=window.WindowState==WindowState.Maximized
                    ?WindowState.Normal:WindowState.Maximized;
                e.Handled=true;
            }
            else if(e.LeftButton==MouseButtonState.Pressed)
            {
                try{window.DragMove();}catch(InvalidOperationException){}
            }
        };
        Grid.SetRow(page,1);
        container.Children.Add(page);
        window.Content=container;
    }

    static Button MakeButton(string label,string tip,Action action)
    {
        var button=new Button{
            Content=label,
            ToolTip=tip,
            Foreground=Brush("Ui2Text","#F3F3F4"),
            Background=Brush("Ui2Sidebar","#141518"),
            BorderThickness=new Thickness(0),
            FontSize=21,
            Padding=new Thickness(0),
            HorizontalContentAlignment=HorizontalAlignment.Center,
            VerticalContentAlignment=VerticalAlignment.Center
        };
        button.Click+=(_,_)=>action();
        return button;
    }

    static Brush Brush(string key,string fallback)=>
        Application.Current.TryFindResource(key) as Brush
        ??(Brush)new BrushConverter().ConvertFromString(fallback)!;
}
