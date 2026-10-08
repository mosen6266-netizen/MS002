using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class AccountGroupWindow : UserControl
{
    readonly ObservableCollection<GroupSelectionRow> _groups=new();
    readonly ObservableCollection<ManagedAccount> _accounts=new();
    bool _loading;

    public AccountGroupWindow()
    {
        InitializeComponent();
        AccountGrid.ItemsSource=_accounts;
        GroupGrid.ItemsSource=_groups;
        Loaded+=async(_,_)=>await RefreshAsync();
    }

    static T Unwrap<T>(string? json)
    {
        if(string.IsNullOrWhiteSpace(json))
            throw new IOException("后台无响应，操作结果无法确定。");
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        if(!root.GetProperty("Ok").GetBoolean())
            throw new IOException(root.TryGetProperty("Error",out var error)
                ?error.GetString():"后台拒绝请求");
        return JsonSerializer.Deserialize<T>(root.GetProperty("Data").GetRawText())
            ??throw new IOException("后台返回格式不正确。");
    }

    async Task RefreshAsync()
    {
        try
        {
            var selected=(AccountGrid.SelectedItem as ManagedAccount)?.Account;
            var raw=await MainWindow.SendAsync(ControlCommands.AccountGroupCatalog,10000);
            var snapshot=Unwrap<AccountGroupOverview>(raw);
            _loading=true;
            _accounts.Clear();
            foreach(var account in snapshot.Accounts) _accounts.Add(account);
            _groups.Clear();
            foreach(var group in snapshot.Groups)
                _groups.Add(new GroupSelectionRow(group));
            AccountGrid.SelectedItem=_accounts.FirstOrDefault(x=>x.Account==selected)
                ??_accounts.FirstOrDefault();
            _loading=false;
            ShowSelectedAccount();
            StatusText.Text=$"已读取 {_accounts.Count} 个账号、{_groups.Count} 个不同群组。";
        }
        catch(Exception ex)
        {
            _loading=false;
            SaveAccountButton.IsEnabled=false;
            StatusText.Text=$"刷新失败：{ex.Message}";
        }
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();

    void AccountGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!_loading) ShowSelectedAccount();
    }

    void ShowSelectedAccount()
    {
        if(AccountGrid.SelectedItem is not ManagedAccount account)
        {
            AccountIdentity.Text="未选择账号";
            AccountLabel.Text="";
            AccountEnabled.IsChecked=false;
            SaveAccountButton.IsEnabled=false;
            return;
        }

        AccountIdentity.Text=account.Account;
        AccountLabel.Text=account.Label;
        AccountEnabled.IsChecked=account.Enabled;
        SaveAccountButton.IsEnabled=true;
    }

    async void SaveAccount_Click(object sender,RoutedEventArgs e)
    {
        if(AccountGrid.SelectedItem is not ManagedAccount account) return;
        var label=AccountLabel.Text.Trim();
        if(label.Length==0)
        {
            StatusText.Text="请填写账号备注。";
            return;
        }
        SaveAccountButton.IsEnabled=false;
        try
        {
            var data=new UpdateManagedAccount(
                account.Account,label,AccountEnabled.IsChecked==true,account.Revision);
            var raw=await MainWindow.SendAsync(ControlCommands.UpdateAccount,10000,data);
            var saved=Unwrap<ManagedAccount>(raw);
            var index=_accounts.IndexOf(account);
            _accounts[index]=saved;
            AccountGrid.SelectedItem=saved;
            StatusText.Text=$"账号「{saved.Label}」的设置已保存，本地版本 {saved.Revision}。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"无法保存账号设置：{ex.Message}。请刷新后重试。";
        }
        finally { SaveAccountButton.IsEnabled=AccountGrid.SelectedItem is ManagedAccount; }
    }

    void SelectAll_Click(object sender,RoutedEventArgs e)
    {
        foreach(var item in _groups) item.Selected=true;
        StatusText.Text="已在界面上全选群组；请点击“保存群组选择”写入本地。";
    }

    void ClearAll_Click(object sender,RoutedEventArgs e)
    {
        foreach(var item in _groups) item.Selected=false;
        StatusText.Text="已在界面上取消全部勾选；请点击“保存群组选择”写入本地。";
    }

    async void SaveGroups_Click(object sender,RoutedEventArgs e)
    {
        GroupGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        GroupGrid.CommitEdit(DataGridEditingUnit.Row,true);
        var ids=_groups.Where(x=>x.Selected).Select(x=>x.GroupId).ToArray();
        try
        {
            var raw=await MainWindow.SendAsync(
                ControlCommands.SetSelectedGroups,10000,new UpdateGroupSelection(ids));
            var count=Unwrap<int>(raw);
            StatusText.Text=$"已保存 {count} 个群组的选择。关闭软件后仍会保留，不会自动启动发送。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"群组选择保存失败：{ex.Message}。请检查后重新保存。";
        }
    }

    void Close_Click(object sender,RoutedEventArgs e)=>
        (Window.GetWindow(this) as MainWindow)?.NavigateHome();
}

public sealed class GroupSelectionRow : INotifyPropertyChanged
{
    bool _selected;

    public GroupSelectionRow(ManagedGroup group)
    {
        GroupId=group.GroupId;
        Name=group.Name;
        MemberAccounts=group.MemberAccounts;
        _selected=group.Selected;
    }

    public string GroupId {get;}
    public string Name {get;}
    public int MemberAccounts {get;}
    public bool Selected
    {
        get=>_selected;
        set
        {
            if(_selected==value) return;
            _selected=value;
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Selected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
