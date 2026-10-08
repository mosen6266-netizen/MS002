using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class ScriptEditorWindow : UserControl
{
    readonly ObservableCollection<ScriptStepRow> _steps=new();
    public ObservableCollection<AccountChoice> AccountOptions {get;}=new();
    bool _firstLoad=true;
    string? _scriptId;
    int _revision;
    bool _loading;
    bool _dirty;
    string? _savedSignature;
    ICollectionView? _scriptView;
    readonly DispatcherTimer _draftTimer=new(){Interval=TimeSpan.FromSeconds(12)};
    static readonly string DraftDirectory=Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SignalSchedulerData","script-drafts");
    // Optional raw text keeps incomplete numeric edits intact across recovery.
    // The optional fields also allow reading drafts written by earlier builds.
    sealed record LocalDraft(string? ScriptId,int Revision,string Name,string Group,
        ScriptEditorStep[] Steps,DateTimeOffset SavedAt,
        string[]? DelayTexts=null,string[]? TypingTexts=null);
    string DraftPath(string? id)=>Path.Combine(DraftDirectory,
        id is null?"new-script.json":"script-"+id.Replace("/","_")
            .Replace("\\","_")+".json");

    void SaveLocalDraft()
    {
        if(_firstLoad || _loading || !_dirty)return;
        try
        {
            if(_savedSignature is not null &&
               ComputeDraftSignature()==_savedSignature)return;
            // Local safety copy only; never writes to the actual scripts table.
            var steps=_steps.Select((row,i)=>new ScriptEditorStep(
                i,row.Account,row.Message,row.Attachment,row.PauseAfter,
                row.ReminderText,int.TryParse(row.DelayText,out var delay)?delay:0,
                int.TryParse(row.TypingText,out var typing)?typing:0)).ToArray();
            Directory.CreateDirectory(DraftDirectory);
            var target=DraftPath(_scriptId);
            var temp=target+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(
                new LocalDraft(_scriptId,_revision,NameBox.Text,GroupBox.Text,
                    steps,DateTimeOffset.UtcNow,
                    _steps.Select(x=>x.DelayText).ToArray(),
                    _steps.Select(x=>x.TypingText).ToArray())));
            File.Move(temp,target,true);
        }
        catch(IOException){ /* Local draft is optional; formal save stays available. */ }
        catch(UnauthorizedAccessException){ }
    }

    void RecoverLocalDraftIfAvailable()
    {
        if(_loading)return;
        try
        {
            var path=DraftPath(_scriptId);
            if(!File.Exists(path))return;
            var draft=JsonSerializer.Deserialize<LocalDraft>(File.ReadAllText(path));
            if(draft is null || draft.ScriptId!=_scriptId ||
               draft.Revision!=_revision)return;
            var sig=JsonSerializer.Serialize(new{
                Name=draft.Name,Group=draft.Group,Steps=draft.Steps.Select((x,i)=>new{
                    x.Account,x.Message,x.Attachment,x.PauseAfter,
                    x.ReminderText,
                    DelayText=i<(draft.DelayTexts?.Length??0)
                        ?draft.DelayTexts![i]:x.DelayAfter.ToString(),
                    TypingText=i<(draft.TypingTexts?.Length??0)
                        ?draft.TypingTexts![i]:x.TypingSeconds.ToString()
                }).ToArray()
            });
            if(sig==_savedSignature)
            {
                File.Delete(path);
                return;
            }
            if(MessageBox.Show(Window.GetWindow(this),
                $"找到 {draft.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 的本地草稿。\\n"+
                "恢复后仍需手动点击保存剧本。是否恢复？",
                "恢复未保存草稿",MessageBoxButton.YesNo,
                MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            _loading=true;
            NameBox.Text=draft.Name;
            GroupBox.Text=draft.Group;
            _steps.Clear();
            for(var i=0;i<draft.Steps.Length;i++)
            {
                var row=new ScriptStepRow(draft.Steps[i]);
                if(i<(draft.DelayTexts?.Length??0))
                    row.DelayText=draft.DelayTexts![i];
                if(i<(draft.TypingTexts?.Length??0))
                    row.TypingText=draft.TypingTexts![i];
                AppendRow(row,-1);
            }
            Reindex();
            _dirty=true;
            StatusText.Text="已恢复未保存草稿，请核对并点击保存剧本。";
        }
        catch(Exception ex) when(ex is IOException or JsonException or
            UnauthorizedAccessException)
        {
            StatusText.Text="本地草稿不可恢复，可继续使用正式保存的数据。";
        }
        finally{_loading=false;}
    }

    void ClearLocalDraft()
    {
        try
        {
            var path=DraftPath(_scriptId);
            if(File.Exists(path))File.Delete(path);
        }
        catch(IOException){ }
        catch(UnauthorizedAccessException){ }
    }

    void ScriptSearchBox_TextChanged(object sender,TextChangedEventArgs e)
    {
        if(_scriptView is null)return;
        _scriptView.Refresh();
        UpdateSearchSummary();
    }
    void UpdateSearchSummary()
    {
        if(ScriptSearchSummary is null || _scriptView is null)return;
        var count=_scriptView.Cast<object>().Count();
        ScriptSearchSummary.Text=$"找到 {count} 个剧本 · 仅筛选列表，不修改草稿";
    }


    public ScriptEditorWindow()
    {
        InitializeComponent();
        StepsGrid.ItemsSource=_steps;
        _draftTimer.Tick+=(_,_)=>SaveLocalDraft();
        Loaded+=(_,_)=>_draftTimer.Start();
        Unloaded+=(_,_)=>{SaveLocalDraft();_draftTimer.Stop();};
        Loaded+=async(_,_)=>
        {
            if(_firstLoad)
            {
                _firstLoad=false;
                await ReloadScriptsAsync(loadFirst:true);
            }
            await LoadAccountOptionsAsync();
        };

    }

    async Task LoadAccountOptionsAsync()
    {
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.AccountGroupCatalog,8500);
            var catalog=ReadData<AccountGroupOverview>(raw);
            AccountOptions.Clear();
            AccountOptions.Add(new AccountChoice("","默认账号（本群在线成员）"));
            foreach(var account in catalog.Accounts.OrderBy(x=>x.Label,StringComparer.CurrentCulture))
            {
                var label=string.IsNullOrWhiteSpace(account.Label) ||
                    account.Label==account.Account
                    ? "未设置备注 · "+LastFour(account.Account)
                    : account.Label;
                var suffix=account.Online?(account.Enabled?" · 在线":" · 已停用"):" · 离线";
                AccountOptions.Add(new AccountChoice(account.Account,label+suffix));
            }
            foreach(var row in _steps) SetDisplayResolver(row);
        }
        catch(Exception ex)
        {
            StatusText.Text="读取账号备注失败，请检查账号管理页面："+ex.Message;
        }
    }

    static string LastFour(string account)=>account.Length>4
        ?account[^4..]:account;

    void SetDisplayResolver(ScriptStepRow row)
    {
        if(!string.IsNullOrWhiteSpace(row.Account) &&
            AccountOptions.All(x=>x.Account!=row.Account))
            AccountOptions.Add(new AccountChoice(row.Account,
                "旧账号 · "+LastFour(row.Account)));
        row.LabelResolver=account=>
            AccountOptions.FirstOrDefault(x=>x.Account==account)?.Label
            ?? (string.IsNullOrWhiteSpace(account)
                ?"默认账号（本群在线成员）":"账号 "+LastFour(account));
        row.RefreshAccountLabel();
    }

    static T ReadData<T>(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))
            throw new IOException("后台没有响应，请确认后台引擎正常。");
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.GetProperty("Ok").GetBoolean())
            throw new IOException(root.TryGetProperty("Error",out var err)
                ?err.GetString():"后台返回错误。");
        return JsonSerializer.Deserialize<T>(root.GetProperty("Data").GetRawText())
            ??throw new IOException("后台返回的数据格式错误。");
    }

    async Task ReloadScriptsAsync(bool loadFirst=false,string? selectId=null)
    {
        var oldId=selectId??_scriptId;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptList,8000);
            var scripts=ReadData<List<ScriptEditorSummary>>(raw);
            _loading=true;
            _scriptView=CollectionViewSource.GetDefaultView(scripts);
            _scriptView.Filter=item=>item is ScriptEditorSummary script &&
                (string.IsNullOrWhiteSpace(ScriptSearchBox.Text) ||
                 script.Name.Contains(ScriptSearchBox.Text.Trim(),
                     StringComparison.CurrentCultureIgnoreCase) ||
                 script.ScriptId==_scriptId);
            ScriptsList.ItemsSource=_scriptView;
            UpdateSearchSummary();
            ScriptsList.SelectedItem=scripts.FirstOrDefault(x=>x.ScriptId==oldId)
                ??(loadFirst?scripts.FirstOrDefault():null);
            _loading=false;

            if(ScriptsList.SelectedItem is ScriptEditorSummary selected &&
               (loadFirst || selectId is not null))
                await LoadScriptAsync(selected.ScriptId);
            else if(loadFirst && scripts.Count==0)
                BeginNew();
        }
        catch(Exception ex)
        {
            _loading=false;
            StatusText.Text=$"无法读取剧本：{ex.Message}";
        }
    }

    async Task LoadScriptAsync(string id)
    {
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptRead,8000,
                new ScriptReadRequest(id));
            var document=ReadData<ScriptEditorDocument>(raw);
            LoadDocument(document);
            StatusText.Text=document.ImportedFromV7
                ?"已加载从 V7 迁移的剧本。编辑不会覆盖原 V7 数据。"
                :"剧本已载入，可以编辑后保存。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"剧本读取失败：{ex.Message}";
        }
    }

    void LoadDocument(ScriptEditorDocument doc)
    {
        _loading=true;
        _scriptId=doc.ScriptId;
        _revision=doc.Revision;
        NameBox.Text=doc.Name;
        GroupBox.Text=doc.TargetGroupId;
        _steps.Clear();
        foreach(var step in doc.Steps)
            AppendRow(new ScriptStepRow(step),-1);
        Reindex();
        _savedSignature=ComputeDraftSignature();
        _dirty=false;
        _loading=false;
        RecoverLocalDraftIfAvailable();
    }

    // A DataGrid CellEditEnding event can fire when only selecting/focusing
    // a cell. Compare actual draft values, not focus/edit event counts.
    string ComputeDraftSignature()=>JsonSerializer.Serialize(new
    {
        Name=NameBox.Text,
        Group=GroupBox.Text,
        Steps=_steps.Select(x=>new{
            x.Account,x.Message,x.Attachment,x.PauseAfter,
            x.ReminderText,x.DelayText,x.TypingText
        }).ToArray()
    });

    async void ScriptsList_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_loading || ScriptsList.SelectedItem is not ScriptEditorSummary next ||
            next.ScriptId==_scriptId) return;
        if(!ConfirmDiscard())
        {
            _loading=true;
            ScriptsList.SelectedItem=(ScriptsList.ItemsSource as IEnumerable<ScriptEditorSummary>)?
                .FirstOrDefault(x=>x.ScriptId==_scriptId);
            _loading=false;
            return;
        }
        await LoadScriptAsync(next.ScriptId);
    }

    public bool CanLeave()=>ConfirmDiscard();

    bool ConfirmDiscard()
    {
        if(!_dirty || (_savedSignature is not null &&
           ComputeDraftSignature()==_savedSignature))
        {
            _dirty=false;
            return true;
        }
        var discard=MessageBox.Show(Window.GetWindow(this),
            "当前剧本有尚未保存的修改。确定放弃这些修改吗？",
            "未保存的修改",MessageBoxButton.YesNo,MessageBoxImage.Warning)
            ==MessageBoxResult.Yes;
        if(discard)
        {
            ClearLocalDraft();
            _dirty=false;
        }
        return discard;
    }

    void BeginNew()
    {
        _loading=true;
        _scriptId=null;
        _revision=0;
        ScriptsList.SelectedItem=null;
        NameBox.Text="新剧本";
        GroupBox.Text="";
        _steps.Clear();
        AppendRow(new ScriptStepRow(),-1);
        Reindex();
        _savedSignature=null;
        _dirty=true;
        _loading=false;
        StatusText.Text="新剧本尚未保存，完成编辑后点击右上角“保存剧本”。";
    }

    async void RestoreVersion_Click(object sender,RoutedEventArgs e)
    {
        if(string.IsNullOrWhiteSpace(_scriptId))
        {
            StatusText.Text="请先打开一个已保存的剧本。";
            return;
        }
        if(!ConfirmDiscard())return;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptVersions,10000,
                new ScriptReadRequest(_scriptId));
            var versions=ReadData<List<ScriptVersionSummary>>(raw);
            if(versions.Count==0)
            {
                StatusText.Text="这个剧本还没有可恢复的旧版本；首次修改保存后才会生成历史。";
                return;
            }
            var names=string.Join("\n",versions.Take(15).Select(x=>
                $"版本 {x.Revision}  ·  {x.Name}  ·  {DateTimeOffset.FromUnixTimeSeconds(x.SavedAt).ToLocalTime():MM-dd HH:mm}"));
            var answer=Microsoft.VisualBasic.Interaction.InputBox(
                "可用历史版本（最近 15 个）：\n"+names+
                "\n\n输入需要查看并恢复的版本号：",
                "剧本修订历史",versions[0].Revision.ToString());
            if(!int.TryParse(answer,out var revision) ||
               !versions.Any(x=>x.Revision==revision))return;
            var versionRaw=await MainWindow.SendAsync(ControlCommands.ScriptVersionRead,
                10000,new ScriptVersionRequest(_scriptId,revision));
            var old=ReadData<ScriptEditorDocument>(versionRaw);
            if(MessageBox.Show(Window.GetWindow(this),
                $"将版本 {revision} 载入当前编辑区？\n正式剧本不会被直接覆盖，核对后需要手动保存。",
                "恢复历史剧本",MessageBoxButton.YesNo,
                MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            var currentRevision=_revision;
            var currentScriptId=_scriptId;
            _loading=true;
            NameBox.Text=old.Name;
            GroupBox.Text=old.TargetGroupId;
            _steps.Clear();
            foreach(var step in old.Steps)AppendRow(new ScriptStepRow(step),-1);
            Reindex();
            _scriptId=currentScriptId;
            _revision=currentRevision;
            _dirty=true;
            _loading=false;
            StatusText.Text=$"旧版本 {revision} 已载入编辑区。请检查内容后点击保存剧本。";
        }
        catch(Exception ex)
        {
            _loading=false;
            StatusText.Text="恢复旧版本失败："+ex.Message;
        }
    }

    void ScriptsList_PreviewMouseRightButtonDown(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if(e.OriginalSource is not System.Windows.DependencyObject node)return;
        while(node is not null)
        {
            if(node is ListBoxItem item)
            {
                item.IsSelected=true;
                break;
            }
            node=System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
    }

    async void RenameScript_Click(object sender,RoutedEventArgs e)
    {
        if(ScriptsList.SelectedItem is not ScriptEditorSummary target)
        {
            StatusText.Text="请先在左侧选择要重命名的剧本。";
            return;
        }
        if(!ConfirmDiscard())return;
        var name=Microsoft.VisualBasic.Interaction.InputBox(
            "输入新的剧本名称：","重命名剧本",target.Name).Trim();
        if(string.IsNullOrWhiteSpace(name) || name==target.Name)return;
        if(name.Length>120)
        {
            StatusText.Text="剧本名称不能超过 120 个字符。";
            return;
        }
        try
        {
            // Always read the selected script's current revision so a context
            // menu action cannot accidentally rename the previously opened one.
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptRead,8000,
                new ScriptReadRequest(target.ScriptId));
            var original=ReadData<ScriptEditorDocument>(raw);
            var request=new ScriptSaveRequest(
                original.ScriptId,name,original.TargetGroupId,
                original.Revision,original.Steps);
            var savedRaw=await MainWindow.SendAsync(
                ControlCommands.ScriptSave,16000,request);
            var saved=ReadData<ScriptEditorDocument>(savedRaw);
            _dirty=false;
            await ReloadScriptsAsync(selectId:saved.ScriptId);
            StatusText.Text="已重命名剧本："+saved.Name;
        }
        catch(Exception ex){StatusText.Text="剧本重命名失败："+ex.Message;}
    }

    void NewScript_Click(object sender,RoutedEventArgs e)
    {
        if(!ConfirmDiscard()) return;
        BeginNew();
    }

    async void ReloadScripts_Click(object sender,RoutedEventArgs e)
    {
        if(!ConfirmDiscard()) return;
        _dirty=false;
        await ReloadScriptsAsync(loadFirst:true);
    }

    async void DeleteScript_Click(object sender,RoutedEventArgs e)
    {
        if(_scriptId is null)
        {
            StatusText.Text="请先在左侧选择要删除的已保存剧本。";
            return;
        }
        var id=_scriptId;
        var name=NameBox.Text;
        var confirmation=MessageBox.Show(Window.GetWindow(this),
            $"确定删除剧本「{name}」吗？\n\n"+
            "仅删除当前 V8 可编辑剧本，不会删除原 V7 备份或已有的发送历史。"+
            "删除后无法在编辑器中撤销。","删除剧本",
            MessageBoxButton.YesNo,MessageBoxImage.Warning);
        if(confirmation!=MessageBoxResult.Yes)return;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptDelete,16000,
                new ScriptDeleteRequest(id,_revision));
            var result=ReadData<ScriptDeleteResult>(raw);
            if(!result.Deleted)throw new IOException("后台未确认删除。");
            _loading=true;
            _scriptId=null;
            _savedSignature=null;
            _dirty=false;
            _loading=false;
            await ReloadScriptsAsync(loadFirst:true);
            StatusText.Text=$"剧本「{name}」已删除。";
        }
        catch(Exception ex)
        {
            StatusText.Text="删除失败："+ex.Message;
        }
    }


    void EditorField_Changed(object sender,TextChangedEventArgs e)
    {
        if(!_loading) _dirty=true;
    }

    // Wait for the native DataGrid selection to finish before opening an editor.
    // Beginning edit on PreviewMouseDown interfered with text selection and
    // caused the first click to be swallowed by a focus change.
    void StepsGrid_PreviewMouseLeftButtonUp(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=System.Windows.Input.MouseButton.Left ||
           e.ClickCount!=1 || e.OriginalSource is not DependencyObject node)return;
        DataGridCell? cell=null;
        while(node is not null)
        {
            // Let existing text editors, dropdowns and row buttons handle input.
            if(node is TextBox || node is ComboBox ||
               node is System.Windows.Controls.Primitives.ButtonBase)return;
            if(node is DataGridCell found){cell=found;break;}
            node=node is Visual
                ?VisualTreeHelper.GetParent(node)
                :System.Windows.LogicalTreeHelper.GetParent(node);
        }
        if(cell is null || cell.IsEditing)return;
        var column=cell.Column;
        var index=StepsGrid.Columns.IndexOf(column);
        if(index is not (2 or 3 or 4 or 5 or 7))return;
        if(cell.DataContext is not ScriptStepRow row)return;

        Dispatcher.BeginInvoke(new Action(()=>
        {
            if(!IsLoaded || !ReferenceEquals(StepsGrid.SelectedItem,row) ||
               cell.IsEditing)return;
            StepsGrid.CurrentCell=new DataGridCellInfo(row,column);
            if(!StepsGrid.BeginEdit())return;
            Dispatcher.BeginInvoke(new Action(()=>
            {
                if(!cell.IsEditing || !ReferenceEquals(StepsGrid.SelectedItem,row))
                    return;
                if(index==2)
                {
                    var combo=FindVisualChild<ComboBox>(cell);
                    if(combo is not null)
                    {
                        combo.Focus();
                        combo.IsDropDownOpen=true;
                    }
                }
                else
                    FindVisualChild<TextBox>(cell)?.Focus();
            }),DispatcherPriority.Input);
        }),DispatcherPriority.Input);
    }

    static T? FindVisualChild<T>(System.Windows.DependencyObject parent)
        where T:System.Windows.DependencyObject
    {
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);
            if(child is T match)return match;
            var deeper=FindVisualChild<T>(child);
            if(deeper is not null)return deeper;
        }
        return null;
    }

    void StepsGrid_CellEditEnding(object sender,DataGridCellEditEndingEventArgs e)
    {
        if(!_loading) _dirty=true;
    }

    void Attach(ScriptStepRow row)
    {
        row.PropertyChanged+=(_,_)=>{if(!_loading)_dirty=true;};
    }

    void AppendRow(ScriptStepRow row,int index)
    {
        Attach(row);
        SetDisplayResolver(row);
        if(index<0 || index>=_steps.Count) _steps.Add(row);
        else _steps.Insert(index,row);
    }

    void Reindex()
    {
        for(var i=0;i<_steps.Count;i++) _steps[i].PositionLabel=i+1;
    }

    void CommitGrid()
    {
        StepsGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        StepsGrid.CommitEdit(DataGridEditingUnit.Row,true);
    }

    void InsertAt(int index)
    {
        CommitGrid();
        var row=new ScriptStepRow();
        AppendRow(row,Math.Clamp(index,0,_steps.Count));
        Reindex();
        _dirty=true;
        StepsGrid.SelectedItem=row;
        StepsGrid.ScrollIntoView(row);
    }

    void Append_Click(object sender,RoutedEventArgs e)=>InsertAt(_steps.Count);

    bool SelectAttachmentRow(object sender)
    {
        if(sender is not MenuItem {CommandParameter:ScriptStepRow row})return false;
        StepsGrid.SelectedItem=row;
        StepsGrid.ScrollIntoView(row);
        return true;
    }
    void AttachmentPreview_Click(object sender,RoutedEventArgs e)
    {
        if(SelectAttachmentRow(sender))PreviewImage_Click(sender,e);
    }
    void AttachmentReplace_Click(object sender,RoutedEventArgs e)
    {
        if(SelectAttachmentRow(sender))ImportImage_Click(sender,e);
    }
    void AttachmentRemove_Click(object sender,RoutedEventArgs e)
    {
        if(!SelectAttachmentRow(sender) || StepsGrid.SelectedItem is not ScriptStepRow row)return;
        if(string.IsNullOrWhiteSpace(row.Attachment))return;
        row.Attachment="";
        _dirty=true;
        StatusText.Text="已从当前消息移除图片引用，请保存剧本。原始图片文件未删除。";
    }

    async void CheckImages_Click(object sender,RoutedEventArgs e)
    {
        var attachments=_steps.Select(x=>x.Attachment)
            .Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToArray();
        if(attachments.Length==0)
        {
            StatusText.Text="当前剧本没有添加图片。";
            return;
        }
        try
        {
            StatusText.Text="正在校验图片完整性…";
            var raw=await MainWindow.SendAsync(ControlCommands.ImageCheck,30000,
                new ImageCheckRequest(attachments));
            var results=ReadData<List<ImageCheckResult>>(raw);
            var good=results.Count(x=>x.Status=="ok");
            var bad=results.Where(x=>x.Status!="ok").ToArray();
            StatusText.Text=$"图片检查：正常 {good} 个，需处理 {bad.Length} 个。";
            if(bad.Length>0)
                MessageBox.Show(Window.GetWindow(this),
                    "下列附件需要处理：\n"+
                    string.Join("\n",bad.Take(15).Select(x=>
                        $"{x.Reference[..Math.Min(32,x.Reference.Length)]}：{x.Detail}"))+
                    (bad.Length>15?"\n其余请逐条检查。":"")+
                    "\n可在对应消息右键“替换图片”。",
                    "图片完整性检查",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        catch(Exception ex)
        {
            StatusText.Text="图片检查失败："+ex.Message;
        }
    }

    async void ImportImage_Click(object sender,RoutedEventArgs e)
    {
        CommitGrid();
        if(StepsGrid.SelectedItem is not ScriptStepRow row)
        {
            StatusText.Text="请先选中要添加图片的那条消息。";
            return;
        }
        var picker=new OpenFileDialog
        {
            Title="为这条消息导入图片",
            Filter="图片文件 (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
            CheckFileExists=true
        };
        if(picker.ShowDialog(Window.GetWindow(this))!=true) return;
        try
        {
            var data=ReadData<ImageAttachmentInfo>(await MainWindow.SendAsync(
                ControlCommands.ImageImport,25000,
                new ImageImportRequest(picker.FileName)));
            row.Attachment=data.Reference;
            _dirty=true;
            StatusText.Text=$"图片「{data.OriginalName}」已安全导入。请保存剧本。"+
                "图片保存在本地用户数据目录，软件升级不会删除。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"图片导入失败：{ex.Message}";
            MessageBox.Show(Window.GetWindow(this),ex.Message,"图片导入失败",
                MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }

    async void PreviewImage_Click(object sender,RoutedEventArgs e)
    {
        CommitGrid();
        if(StepsGrid.SelectedItem is not ScriptStepRow row ||
            string.IsNullOrWhiteSpace(row.Attachment))
        {
            StatusText.Text="请先选中包含图片附件的那条消息。";
            return;
        }
        try
        {
            // Imported V7 scripts can still contain local file paths instead of
            // V8 content-addressed references. Safely import a surviving source
            // on first preview without changing the original backup.
            if(!row.Attachment.StartsWith("img:",StringComparison.Ordinal))
            {
                if(!File.Exists(row.Attachment))
                    throw new FileNotFoundException(
                        "旧版图片原路径不存在。请右键选择“替换图片”，从本机重新选择。",
                        row.Attachment);
                var migrated=ReadData<ImageAttachmentInfo>(await MainWindow.SendAsync(
                    ControlCommands.ImageImport,25000,
                    new ImageImportRequest(row.Attachment)));
                row.Attachment=migrated.Reference;
                _dirty=true;
                StatusText.Text="已恢复旧版图片，请保存剧本以保留新的附件引用。";
            }
            var info=ReadData<ImageAttachmentInfo>(await MainWindow.SendAsync(
                ControlCommands.ImageLookup,15000,
                new ImageLookupRequest(row.Attachment)));
            var bitmap=new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption=BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth=1400;
            bitmap.UriSource=new Uri(info.AbsolutePath,UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            var window=new Window
            {
                Owner=Window.GetWindow(this),
                Title=$"预览图片 - {info.OriginalName}",
                Width=850,Height=670,
                MinWidth=500,MinHeight=380,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Background=Brushes.Black,
                Content=new Border
                {
                    Padding=new Thickness(15),
                    Child=new Image{Source=bitmap,Stretch=Stretch.Uniform}
                }
            };
            window.ShowDialog();
            StatusText.Text="本地附件图片校验通过，已打开预览窗口。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"无法预览图片：{ex.Message}";
            MessageBox.Show(Window.GetWindow(this),ex.Message,"图片预览失败",
                MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }



    void InsertAbove_Click(object sender,RoutedEventArgs e)
    {
        if((sender as Button)?.Tag is ScriptStepRow row)
            InsertAt(_steps.IndexOf(row));
    }

    void InsertBelow_Click(object sender,RoutedEventArgs e)
    {
        if((sender as Button)?.Tag is ScriptStepRow row)
            InsertAt(_steps.IndexOf(row)+1);
    }

    void DeleteStep_Click(object sender,RoutedEventArgs e)
    {
        if((sender as Button)?.Tag is ScriptStepRow row)
        {
            CommitGrid();
            _steps.Remove(row);
            Reindex();
            _dirty=true;
        }
    }

    void Move(int direction)
    {
        CommitGrid();
        if(StepsGrid.SelectedItem is not ScriptStepRow row) return;
        var index=_steps.IndexOf(row);
        var next=index+direction;
        if(next<0 || next>=_steps.Count) return;
        _steps.Move(index,next);
        Reindex();
        _dirty=true;
        StepsGrid.SelectedItem=row;
        StepsGrid.ScrollIntoView(row);
    }

    void MoveUp_Click(object sender,RoutedEventArgs e)=>Move(-1);
    void MoveDown_Click(object sender,RoutedEventArgs e)=>Move(1);

    ScriptSaveRequest CollectDraft()
    {
        CommitGrid();
        if(string.IsNullOrWhiteSpace(NameBox.Text))
            throw new ArgumentException("请输入剧本名称。");
        var output=new List<ScriptEditorStep>();
        foreach(var (row,i) in _steps.Select((x,i)=>(x,i)))
        {
            if(!int.TryParse(row.DelayText,out var delay) || delay is <0 or >3600)
                throw new ArgumentException($"第 {i+1} 条的间隔必须是 0～3600 秒整数。");
            if(!int.TryParse(row.TypingText,out var typing) || typing is <0 or >300)
                throw new ArgumentException($"第 {i+1} 条的输入时间必须是 0～300 秒整数。");
            output.Add(new ScriptEditorStep(i,row.Account,row.Message,row.Attachment,
                row.PauseAfter,row.ReminderText,delay,typing));
        }
        return new ScriptSaveRequest(_scriptId,NameBox.Text.Trim(),GroupBox.Text,
            _revision,output);
    }

    async void Save_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var request=CollectDraft();
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptSave,16000,request);
            var doc=ReadData<ScriptEditorDocument>(raw);
            ClearLocalDraft();
            LoadDocument(doc);
            StatusText.Text=$"保存成功：{doc.Name}，共 {doc.Steps.Count} 条消息。";
            await ReloadScriptsAsync(selectId:doc.ScriptId);
        }
        catch(Exception ex)
        {
            StatusText.Text=$"保存失败：{ex.Message}。当前内容仍在编辑器中。";
            MessageBox.Show(Window.GetWindow(this),ex.Message,"无法保存剧本",MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    void Import_Click(object sender,RoutedEventArgs e)
    {
        if(!ConfirmDiscard()) return;
        var picker=new OpenFileDialog
        {
            Title="导入剧本 JSON",
            Filter="JSON 文件 (*.json)|*.json",
            CheckFileExists=true
        };
        if(picker.ShowDialog(Window.GetWindow(this))!=true) return;
        try
        {
            if(new FileInfo(picker.FileName).Length>8*1024*1024)
                throw new InvalidDataException("文件超过 8 MB，无法导入。");
            var data=File.ReadAllText(picker.FileName);
            var draft=JsonSerializer.Deserialize<ScriptSaveRequest>(
                data,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
                ??throw new InvalidDataException("JSON 文件中没有有效剧本。");

            _loading=true;
            _scriptId=null; // Import is always a new copy, never overwrite by ID.
            _revision=0;
            ScriptsList.SelectedItem=null;
            NameBox.Text=draft.Name??"导入剧本";
            GroupBox.Text=draft.TargetGroupId??"";
            _steps.Clear();
            foreach(var step in draft.Steps??Array.Empty<ScriptEditorStep>())
                AppendRow(new ScriptStepRow(step),-1);
            Reindex();
            _loading=false;
            _savedSignature=null;
            _dirty=true;
            StatusText.Text="导入成功（未保存）。请检查消息及附件路径后点击“保存剧本”。";
        }
        catch(Exception ex)
        {
            _loading=false;
            StatusText.Text=$"导入失败：{ex.Message}";
        }
    }

    void Export_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var draft=CollectDraft();
            var picker=new SaveFileDialog
            {
                Title="导出剧本",
                Filter="JSON 文件 (*.json)|*.json",
                FileName="剧本.json",
                AddExtension=true
            };
            if(picker.ShowDialog(Window.GetWindow(this))!=true) return;
            // Metadata and original attachment path only, no photo binaries.
            File.WriteAllText(picker.FileName,
                JsonSerializer.Serialize(draft with{ScriptId=null,Revision=0},
                    new JsonSerializerOptions{WriteIndented=true}));
            StatusText.Text="剧本已导出 JSON。附件图片本体不包含在该文件中。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"导出失败：{ex.Message}";
        }
    }

    void Close_Click(object sender,RoutedEventArgs e)=>
        (Window.GetWindow(this) as MainWindow)?.NavigateHome();
}

public sealed record AccountChoice(string Account,string Label);

public sealed class ScriptStepRow : INotifyPropertyChanged
{
    string _account="",_message="",_attachment="",_reminder="",_delay="5",_typing="5";
    bool _pauseAfter;
    int _positionLabel;

    public ScriptStepRow(){}
    public ScriptStepRow(ScriptEditorStep step)
    {
        _account=step.Account;
        _message=step.Message;
        _attachment=step.Attachment;
        _reminder=step.ReminderText;
        _delay=step.DelayAfter.ToString();
        _typing=step.TypingSeconds.ToString();
        _pauseAfter=step.PauseAfter;
    }

    public int PositionLabel{get=>_positionLabel;set=>Set(ref _positionLabel,value);}
    public Func<string,string>? LabelResolver {get;set;}
    public string AccountLabel=>LabelResolver?.Invoke(Account)
        ?? (string.IsNullOrWhiteSpace(Account)?"默认账号":("账号 "+Account[^Math.Min(4,Account.Length)..]));
    public void RefreshAccountLabel()=>PropertyChanged?.Invoke(
        this,new PropertyChangedEventArgs(nameof(AccountLabel)));
    public string AttachmentLabel=>string.IsNullOrWhiteSpace(Attachment)?"—":"已添加图片";
    public string Account
    {
        get=>_account;
        set
        {
            Set(ref _account,value??"");
            RefreshAccountLabel();
        }
    }
    public string Message{get=>_message;set=>Set(ref _message,value);}
    public string Attachment
    {
        get=>_attachment;
        set
        {
            Set(ref _attachment,value);
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(AttachmentLabel)));
        }
    }
    public string ReminderText{get=>_reminder;set=>Set(ref _reminder,value);}
    public string DelayText{get=>_delay;set=>Set(ref _delay,value);}
    public string TypingText{get=>_typing;set=>Set(ref _typing,value);}
    public bool PauseAfter{get=>_pauseAfter;set=>Set(ref _pauseAfter,value);}

    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field,T value,[CallerMemberName]string? name=null)
    {
        if(EqualityComparer<T>.Default.Equals(field,value)) return;
        field=value;
        PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
    }
}
