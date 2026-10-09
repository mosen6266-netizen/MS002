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
    bool _refreshing;
    bool _savingGroups;

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
        if(_refreshing || _savingGroups)return;
        _refreshing=true;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.AccountGroupCatalog,10000);
            var snapshot=Unwrap<AccountGroupOverview>(raw);
            // Capture after IPC: user may have edited while the call was pending.
            var currentAccount=AccountGrid.SelectedItem as ManagedAccount;
            var selected=currentAccount?.Account;
            var unsavedLabel=AccountLabel.Text;
            var unsavedEnabled=AccountEnabled.IsChecked==true;
            var hasAccountEdits=currentAccount is not null &&
                (unsavedLabel!=currentAccount.Label || unsavedEnabled!=currentAccount.Enabled);
            var editedGroups=_groups.Where(x=>x.Selected!=x.SavedSelected)
                .ToDictionary(x=>x.GroupId,x=>x.Selected,StringComparer.Ordinal);
            _loading=true;
            try
            {
                _accounts.Clear();
                foreach(var account in snapshot.Accounts)_accounts.Add(account);
                _groups.Clear();
                foreach(var group in snapshot.Groups)
                {
                    var row=new GroupSelectionRow(group);
                    if(editedGroups.TryGetValue(row.GroupId,out var value))row.Selected=value;
                    _groups.Add(row);
                }
                AccountGrid.SelectedItem=_accounts.FirstOrDefault(x=>x.Account==selected)
                    ??_accounts.FirstOrDefault();
            }
            finally{_loading=false;}
            ShowSelectedAccount();
            if(hasAccountEdits &&
                (AccountGrid.SelectedItem as ManagedAccount)?.Account==selected)
            {
                AccountLabel.Text=unsavedLabel;
                AccountEnabled.IsChecked=unsavedEnabled;
            }
            StatusText.Text=$"已读取 {_accounts.Count} 个账号、{_groups.Count} 个不同群组。"+
                (hasAccountEdits || editedGroups.Count>0
                    ?" 未保存的修改已保留，请核对后保存。":"");
        }
        catch(Exception ex)
        {
            StatusText.Text=$"刷新失败：{ex.Message}。现有表单内容仍保留。";
        }
        finally{_refreshing=false;}
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
        if(_refreshing || AccountGrid.SelectedItem is not ManagedAccount account)return;
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
        if(_refreshing || _savingGroups)return;
        _savingGroups=true;
        GroupGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        GroupGrid.CommitEdit(DataGridEditingUnit.Row,true);
        var ids=_groups.Where(x=>x.Selected).Select(x=>x.GroupId).ToArray();
        try
        {
            var raw=await MainWindow.SendAsync(
                ControlCommands.SetSelectedGroups,10000,new UpdateGroupSelection(ids));
            var count=Unwrap<int>(raw);
            var selectedIds=ids.ToHashSet(StringComparer.Ordinal);
            foreach(var group in _groups)
                group.MarkSaved(selectedIds.Contains(group.GroupId));
            StatusText.Text=$"已保存 {count} 个群组的选择。关闭软件后仍会保留，不会自动启动发送。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"群组选择保存失败：{ex.Message}。请检查后重新保存。";
        }
        finally{_savingGroups=false;}
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
        SavedSelected=group.Selected;
    }

    public bool SavedSelected {get;private set;}
    public void MarkSaved(bool saved)=>SavedSelected=saved;

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
