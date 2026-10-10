using System.Windows;
using System.Windows.Media;

namespace SignalScheduler.Desktop;

/// <summary>
/// UI 2.0's owner-bound dark dialog, preserving WPF MessageBoxResult semantics.
/// No IPC, database writes or background work occur inside the dialog.
/// </summary>
public partial class Ui2DialogWindow : Window
{
    public MessageBoxResult Selection {get;private set;}
    readonly MessageBoxButton _buttons;

    public Ui2DialogWindow(string body,string title,MessageBoxButton buttons,
        MessageBoxImage image)
    {
        InitializeComponent();
        _buttons=buttons;
        DialogHeading.Text=string.IsNullOrWhiteSpace(title)?"MS002 · 提醒":title;
        DialogMessage.Text=body??"";
        Selection=buttons switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel=>MessageBoxResult.No,
            MessageBoxButton.OKCancel=>MessageBoxResult.Cancel,
            _=>MessageBoxResult.OK
        };
        switch(image)
        {
            case MessageBoxImage.Error:
            case MessageBoxImage.Warning:
                DialogIcon.Text="!";
                DialogIcon.Foreground=GetBrush("Ui2Warning",Brushes.White);
                Topmost=true;
                break;
            case MessageBoxImage.Information:
                DialogIcon.Text="i";
                break;
            case MessageBoxImage.Question:
                DialogIcon.Text="?";
                break;
            default:
                DialogIcon.Text="i";
                break;
        }
        SecondaryButton.Visibility=buttons==MessageBoxButton.OK
            ?Visibility.Collapsed:Visibility.Visible;
        PrimaryButton.Content=buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
            ?"确认":"确定";
        SecondaryButton.Content=buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
            ?"取消":buttons==MessageBoxButton.OKCancel?"取消":"";
        PrimaryButton.Click+=(_,_)=>{
            Selection=buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
                ?MessageBoxResult.Yes:MessageBoxResult.OK;
            Close();
        };
        SecondaryButton.Click+=(_,_)=>{
            Selection=buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
                ?MessageBoxResult.No:MessageBoxResult.Cancel;
            Close();
        };
        PreviewKeyDown+=(_,e)=>{
            if(e.Key==System.Windows.Input.Key.Escape)
            {
                Selection=buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
                    ?MessageBoxResult.No:MessageBoxResult.Cancel;
                Close();
                e.Handled=true;
            }
        };
    }

    static Brush GetBrush(string key,Brush fallback)=>
        Application.Current.TryFindResource(key) as Brush??fallback;
}

internal static class Ui2MessageBox
{
    public static MessageBoxResult Show(Window? owner,string text,string caption,
        MessageBoxButton buttons,MessageBoxImage icon)
    {
        if(Application.Current is null ||
           !Application.Current.Dispatcher.CheckAccess())
            return System.Windows.MessageBox.Show(owner,text,caption,buttons,icon);
        var dialog=new Ui2DialogWindow(text,caption,buttons,icon);
        if(owner is {IsLoaded:true,IsVisible:true} &&
           owner.WindowState!=WindowState.Minimized)
            dialog.Owner=owner;
        else
            dialog.WindowStartupLocation=WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Selection;
    }
}
