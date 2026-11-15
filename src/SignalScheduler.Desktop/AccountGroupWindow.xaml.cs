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
    bool _savingAccount;
    bool _managingAccount;

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
        if(_refreshing || _savingGroups || _savingAccount || _managingAccount)return;
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
        finally
        {
            _refreshing=false;
            UpdateAccountActions();
        }
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();

    void AccountGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!_loading) ShowSelectedAccount();
    }

    void UpdateAccountActions()
    {
        var available=AccountGrid.SelectedItem is ManagedAccount &&
            !_refreshing && !_savingAccount && !_managingAccount;
        SaveAccountButton.IsEnabled=available;
        RelinkAccountButton.IsEnabled=available;
        RemoveAccountButton.IsEnabled=available;
    }

    void ShowSelectedAccount()
    {
        if(AccountGrid.SelectedItem is not ManagedAccount account)
        {
            AccountIdentity.Text="未选择账号";
            AccountLabel.Text="";
            AccountEnabled.IsChecked=false;
            UpdateAccountActions();
            return;
        }

        AccountIdentity.Text=account.Account;
        AccountLabel.Text=account.Label;
        AccountEnabled.IsChecked=account.Enabled;
        UpdateAccountActions();
    }

    async void SaveAccount_Click(object sender,RoutedEventArgs e)
    {
        if(_refreshing || _savingAccount || _managingAccount ||
           AccountGrid.SelectedItem is not ManagedAccount account)return;
        var label=AccountLabel.Text.Trim();
        var enabled=AccountEnabled.IsChecked==true;
        if(label.Length==0)
        {
            StatusText.Text="请填写账号备注。";
            return;
        }
        _savingAccount=true;
        UpdateAccountActions();
        try
        {
            var data=new UpdateManagedAccount(
                account.Account,label,enabled,account.Revision);
            var raw=await MainWindow.SendAsync(ControlCommands.UpdateAccount,10000,data);
            var saved=Unwrap<ManagedAccount>(raw);
            var sameSelection=(AccountGrid.SelectedItem as ManagedAccount)?.Account==saved.Account;
            var updatedWhileSaving=sameSelection &&
                (AccountLabel.Text!=label || AccountEnabled.IsChecked!=enabled);
            var editedLabel=AccountLabel.Text;
            var editedEnabled=AccountEnabled.IsChecked;
            var index=_accounts.ToList().FindIndex(x=>x.Account==saved.Account);
            if(index>=0)_accounts[index]=saved;
            if(sameSelection && index>=0)
            {
                AccountGrid.SelectedItem=saved;
                if(updatedWhileSaving)
                {
                    AccountLabel.Text=editedLabel;
                    AccountEnabled.IsChecked=editedEnabled;
                }
            }
            StatusText.Text=$"账号「{saved.Label}」的设置已保存，本地版本 {saved.Revision}。"+
                (updatedWhileSaving?" 后续修改仍在编辑框中，请再次保存。":"");
        }
        catch(Exception ex)
        {
            StatusText.Text=$"无法保存账号设置：{ex.Message}。请刷新后重试。";
        }
        finally
        {
            _savingAccount=false;
            UpdateAccountActions();
        }
    }

    async void RelinkAccount_Click(object sender,RoutedEventArgs e)=>
        await ManageLocalAccountAsync(true);

    async void RemoveAccount_Click(object sender,RoutedEventArgs e)=>
        await ManageLocalAccountAsync(false);

    async Task ManageLocalAccountAsync(bool forRelink)
    {
        if(_refreshing || _savingAccount || _savingGroups || _managingAccount ||
           AccountGrid.SelectedItem is not ManagedAccount account)return;

        var warning=forRelink
            ? $"确定重新扫码登录「{account.Label}」（{account.Account}）吗？\n\n"+
              "这会先清理这个账号在本机 signal-cli 的旧登录数据，然后显示新二维码。"+
              "必须使用对应的手机 Signal 扫码。任务历史、备注和其他账号不会删除。"
            : $"确定从本机删除「{account.Label}」（{account.Account}）吗？\n\n"+
              "这会删除该账号在本机 signal-cli 的登录数据，并从账号列表中移除。"+
              "不会删除手机上的 Signal 主账号或其他账号；既有任务历史保留。"+
              "如需恢复，之后可以重新扫码连接。";
        var title=forRelink?"确认重新扫码登录":"确认删除本机账号";
        if(MessageBox.Show(Window.GetWindow(this),warning,title,
                MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
            return;

        _managingAccount=true;
        UpdateAccountActions();
        var succeeded=false;
        var linked=false;
        try
        {
            StatusText.Text=forRelink
                ?"正在清理所选账号的旧本机登录数据…"
                :"正在从本机移除选中的账号…";
            var request=new RemoveManagedAccountRequest(
                account.Account,account.Revision,forRelink);
            var raw=await MainWindow.SendAsync(ControlCommands.RemoveLocalAccount,30000,request);
            _=Unwrap<RemoveManagedAccountResult>(raw);
            succeeded=true;

            if(forRelink)
            {
                StatusText.Text="旧登录数据已清理，请使用对应的手机重新扫码。";
                var parent=Window.GetWindow(this);
                if(parent is null)throw new IOException("无法打开扫码窗口。");
                var dialog=new LinkAccountWindow(account.Account,account.Label){Owner=parent};
                linked=dialog.ShowDialog()==true;
            }
        }
        catch(Exception ex)
        {
            StatusText.Text=$"账号操作未完成：{ex.Message}";
        }
        finally
        {
            _managingAccount=false;
            UpdateAccountActions();
            if(succeeded)
            {
                await RefreshAsync();
                StatusText.Text=forRelink
                    ?linked
                        ?$"账号 {account.Account} 已重新扫码连接；备注和任务历史保留。"
                        :$"账号 {account.Account} 的旧本机登录数据已清理；尚未完成扫码，稍后可再次操作。"
                    :$"账号 {account.Account} 已从本机移除；手机 Signal 账号及历史任务未被删除。";
            }
        }
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
