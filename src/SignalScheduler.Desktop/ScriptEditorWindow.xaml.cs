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
using System.Windows.Input;
using Microsoft.Win32;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class ScriptEditorWindow : UserControl
{
    readonly ObservableCollection<ScriptStepRow> _steps=new();
    public ObservableCollection<AccountChoice> AccountOptions {get;}=new();
    bool _firstLoad=true;
    // Ignore responses from older overlapping script loads.
    int _scriptLoadGeneration;
    string? _scriptId;
    int _revision;
    bool _loading;
    bool _dirty;
    string? _savedSignature;
    ICollectionView? _scriptView;
    Point _scriptDragStart;
    ScriptEditorSummary? _scriptDragItem;
    bool _scriptReorderBusy;
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
               draft.Revision!=_revision || draft.Steps is null ||
               draft.Steps.Length>1500)return;
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
                $"找到 {draft.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 的本地草稿。\n"+
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

    void ScriptsList_PreviewMouseLeftButtonDown(object sender,MouseButtonEventArgs e)
    {
        _scriptDragStart=e.GetPosition(ScriptsList);
        _scriptDragItem=ItemsControl.ContainerFromElement(
            ScriptsList,e.OriginalSource as DependencyObject) is ListBoxItem item
            ?item.DataContext as ScriptEditorSummary:null;
    }

    void ScriptsList_PreviewMouseMove(object sender,MouseEventArgs e)
    {
        if(_scriptReorderBusy || _bundleBusy || _scriptDragItem is null ||
           e.LeftButton!=MouseButtonState.Pressed ||
           !string.IsNullOrWhiteSpace(ScriptSearchBox.Text))return;
        var p=e.GetPosition(ScriptsList);
        if(Math.Abs(p.X-_scriptDragStart.X)<SystemParameters.MinimumHorizontalDragDistance &&
           Math.Abs(p.Y-_scriptDragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        var dragged=_scriptDragItem;
        _scriptDragItem=null;
        DragDrop.DoDragDrop(ScriptsList,
            new DataObject(typeof(ScriptEditorSummary),dragged),
            DragDropEffects.Move);
    }

    void ScriptsList_DragOver(object sender,DragEventArgs e)
    {
        e.Effects=!_scriptReorderBusy &&
            string.IsNullOrWhiteSpace(ScriptSearchBox.Text) &&
            e.Data.GetDataPresent(typeof(ScriptEditorSummary))
            ?DragDropEffects.Move:DragDropEffects.None;
        e.Handled=true;
    }

    async void ScriptsList_Drop(object sender,DragEventArgs e)
    {
        e.Handled=true;
        if(_scriptReorderBusy || !string.IsNullOrWhiteSpace(ScriptSearchBox.Text))
        {
            StatusText.Text="请先清空搜索框再拖动排序，以免隐藏剧本改变顺序。";
            return;
        }
        var from=e.Data.GetData(typeof(ScriptEditorSummary)) as ScriptEditorSummary;
        var target=(ItemsControl.ContainerFromElement(ScriptsList,
            e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext
            as ScriptEditorSummary;
        if(from is null || target is null || from.ScriptId==target.ScriptId)return;
        var rows=_scriptView?.Cast<ScriptEditorSummary>().ToList();
        if(rows is null || rows.Count<2)return;
        var fromIndex=rows.FindIndex(x=>x.ScriptId==from.ScriptId);
        var toIndex=rows.FindIndex(x=>x.ScriptId==target.ScriptId);
        if(fromIndex<0 || toIndex<0)return;
        rows.RemoveAt(fromIndex);
        rows.Insert(toIndex,from);
        _scriptReorderBusy=true;
        try
        {
            ReadData<bool>(await MainWindow.SendAsync(
                ControlCommands.ScriptReorder,14000,
                new ScriptReorderRequest(rows.Select(x=>x.ScriptId).ToArray())));
            await ReloadScriptsAsync();
            StatusText.Text="剧本列表顺序已保存，重启后仍然有效。";
        }
        catch(Exception ex)
        {
            StatusText.Text="排序保存失败："+ex.Message+"；列表将重新读取。";
            await ReloadScriptsAsync();
        }
        finally{_scriptReorderBusy=false;}
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
        var generation=_scriptLoadGeneration;
        var editorSignature=ComputeDraftSignature();
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptList,8000);
            // A background list refresh must not overwrite a new script or
            // invalidate edits made while the IPC request was pending.
            if(generation!=_scriptLoadGeneration ||
               editorSignature!=ComputeDraftSignature() ||
               oldId!=(selectId??_scriptId))return;
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
        var generation=++_scriptLoadGeneration;
        var before=ComputeDraftSignature();
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptRead,8000,
                new ScriptReadRequest(id));
            // Slow responses must never replace a newer user selection.
            if(generation!=_scriptLoadGeneration)return;
            if(before!=ComputeDraftSignature())
            {
                StatusText.Text="加载期间编辑内容发生变化，已保留当前内容；需要时请重新选择剧本。";
                return;
            }
            var document=ReadData<ScriptEditorDocument>(raw);
            if(document.ScriptId!=id)
                throw new IOException("后台返回的剧本与所选剧本不一致。");
            LoadDocument(document);
            StatusText.Text=document.ImportedFromV7
                ?"已加载从 V7 迁移的剧本。编辑不会覆盖原 V7 数据。"
                :"剧本已载入，可以编辑后保存。";
        }
        catch(Exception ex)
        {
            if(generation==_scriptLoadGeneration)
            {
                // Keep the selected list entry consistent with the document
                // that is actually displayed after a failed asynchronous read.
                _loading=true;
                try
                {
                    ScriptsList.SelectedItem=
                        (ScriptsList.ItemsSource as System.Collections.IEnumerable)?
                            .Cast<ScriptEditorSummary>()
                            .FirstOrDefault(x=>x.ScriptId==_scriptId);
                }
                finally{_loading=false;}
                StatusText.Text=$"剧本读取失败：{ex.Message}";
            }
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
        // Invalidate outstanding reads so a late response cannot overwrite
        // a new unsaved script or its local draft.
        ++_scriptLoadGeneration;
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

    int? ChooseScriptVersion(IReadOnlyList<ScriptVersionSummary> versions)
    {
        var owner=Window.GetWindow(this);
        var popup=new Window
        {
            Owner=owner,
            Title="选择剧本历史版本",
            Width=650,
            Height=490,
            MinWidth=470,
            MinHeight=330,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Background=System.Windows.Media.Brushes.White,
            ResizeMode=ResizeMode.CanResize
        };
        var layout=new Grid{Margin=new Thickness(18)};
        layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var instruction=new TextBlock
        {
            Text="选择要载入编辑区的历史版本（不会直接覆盖正式保存的数据）",
            TextWrapping=TextWrapping.Wrap,
            Foreground=System.Windows.Media.Brushes.Black,
            Margin=new Thickness(0,0,0,12)
        };
        layout.Children.Add(instruction);
        var list=new ListBox
        {
            ItemsSource=versions.Select(x=>
                $"版本 {x.Revision}　 {x.Name}　 {DateTimeOffset.FromUnixTimeSeconds(x.SavedAt).ToLocalTime():yyyy-MM-dd HH:mm}").ToArray(),
            SelectedIndex=0,
            FontSize=14,
            Foreground=System.Windows.Media.Brushes.Black,
            Background=System.Windows.Media.Brushes.White
        };
        Grid.SetRow(list,1);
        layout.Children.Add(list);
        var actions=new StackPanel
        {
            Orientation=Orientation.Horizontal,
            HorizontalAlignment=HorizontalAlignment.Right,
            Margin=new Thickness(0,14,0,0)
        };
        var cancel=new Button{Content="取消",MinWidth=100,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,10,0)};
        cancel.Click+=(_,_)=>popup.DialogResult=false;
        var accept=new Button{Content="载入选中版本",MinWidth=135,Padding=new Thickness(12,6,12,6)};
        accept.Click+=(_,_)=>{if(list.SelectedIndex>=0)popup.DialogResult=true;};
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        Grid.SetRow(actions,2);
        layout.Children.Add(actions);
        popup.Content=layout;
        if(popup.ShowDialog()!=true || list.SelectedIndex<0)return null;
        return versions[list.SelectedIndex].Revision;
    }

    async void RestoreVersion_Click(object sender,RoutedEventArgs e)
    {
        if(string.IsNullOrWhiteSpace(_scriptId))
        {
            StatusText.Text="请先打开一个已保存的剧本。";
            return;
        }
        var scriptId=_scriptId;
        var generation=_scriptLoadGeneration;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptVersions,10000,
                new ScriptReadRequest(scriptId));
            if(generation!=_scriptLoadGeneration || _scriptId!=scriptId)return;
            var versions=ReadData<List<ScriptVersionSummary>>(raw);
            if(versions.Count==0)
            {
                StatusText.Text="这个剧本还没有可恢复的旧版本；首次修改保存后才会生成历史。";
                return;
            }
            var revision=ChooseScriptVersion(versions);
            if(revision is null)return;
            var versionRaw=await MainWindow.SendAsync(ControlCommands.ScriptVersionRead,
                10000,new ScriptVersionRequest(scriptId,revision.Value));
            if(generation!=_scriptLoadGeneration || _scriptId!=scriptId)
            {
                StatusText.Text="读取期间切换了剧本，已取消旧版本恢复。";
                return;
            }
            var old=ReadData<ScriptEditorDocument>(versionRaw);
            if(old.ScriptId!=scriptId)
                throw new IOException("后台返回的历史版本与当前剧本不一致。");
            var original=ComputeDraftSignature();
            if(MessageBox.Show(Window.GetWindow(this),
                $"将版本 {revision.Value} 载入当前编辑区？\n正式剧本不会被直接覆盖，核对后需要手动保存。",
                "恢复历史剧本",MessageBoxButton.YesNo,
                MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            if(generation!=_scriptLoadGeneration || _scriptId!=scriptId ||
               original!=ComputeDraftSignature())
            {
                StatusText.Text="确认期间编辑内容发生变化，已取消恢复。";
                return;
            }
            if(!ConfirmDiscard())return;
            var currentRevision=_revision;
            _loading=true;
            try
            {
                NameBox.Text=old.Name;
                GroupBox.Text=old.TargetGroupId;
                _steps.Clear();
                foreach(var step in old.Steps)
                    AppendRow(new ScriptStepRow(step),-1);
                Reindex();
                _scriptId=scriptId;
                _revision=currentRevision;
                _dirty=true;
            }
            finally{_loading=false;}
            StatusText.Text=$"历史版本 {revision.Value} 已载入编辑区。检查内容后点击保存剧本。";
        }
        catch(Exception ex)
        {
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

    void AttachmentQuickPreview_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button {Tag:ScriptStepRow row})return;
        CommitGrid();
        StepsGrid.SelectedItem=row;
        StepsGrid.ScrollIntoView(row);
        if(string.IsNullOrWhiteSpace(row.Attachment))
            ImportImage_Click(sender,e);
        else
            PreviewImage_Click(sender,e);
    }

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
        var imageRows=_steps.Where(x=>!string.IsNullOrWhiteSpace(x.Attachment))
            .Select(x=>(Row:x,Reference:x.Attachment)).ToArray();
        var attachments=imageRows.Select(x=>x.Reference)
            .Distinct(StringComparer.Ordinal).ToArray();
        if(attachments.Length==0)
        {
            StatusText.Text="当前剧本没有添加图片。";
            return;
        }
        try
        {
            StatusText.Text="正在校验图片完整性…";
            var generation=_scriptLoadGeneration;
            var raw=await MainWindow.SendAsync(ControlCommands.ImageCheck,30000,
                new ImageCheckRequest(attachments));
            if(generation!=_scriptLoadGeneration ||
               imageRows.Length!=_steps.Count(x=>!string.IsNullOrWhiteSpace(x.Attachment)) ||
               imageRows.Any(x=>!_steps.Contains(x.Row) ||
                   x.Row.Attachment!=x.Reference))
            {
                StatusText.Text="图片检查期间剧本或附件已变化，请重新检查。";
                return;
            }
            var results=ReadData<List<ImageCheckResult>>(raw);
            var byReference=results.ToDictionary(x=>x.Reference,StringComparer.Ordinal);
            var good=0;
            var problemRows=new List<string>();
            foreach(var row in _steps)
            {
                if(string.IsNullOrWhiteSpace(row.Attachment))continue;
                if(!byReference.TryGetValue(row.Attachment,out var check))
                {
                    row.SetImageHealth("invalid","未返回图片校验结果");
                    problemRows.Add($"第 {row.PositionLabel} 句：未返回图片校验结果");
                    continue;
                }
                row.SetImageHealth(check.Status,check.Detail);
                if(check.Status=="ok")good++;
                else problemRows.Add($"第 {row.PositionLabel} 句：{check.Detail}");
            }
            StatusText.Text=$"图片检查：正常 {good} 条消息，需处理 {problemRows.Count} 条消息。";
            if(problemRows.Count>0)
                MessageBox.Show(Window.GetWindow(this),
                    "以下消息的图片需要处理：\n"+
                    string.Join("\n",problemRows.Take(15))+
                    (problemRows.Count>15?"\n还有其他异常，请查看附件列。":"")+
                    "\n在对应消息的附件列右键可以替换图片。",
                    "图片完整性检查",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        catch(Exception ex)
        {
            StatusText.Text="图片检查失败："+ex.Message;
        }
    }

    bool _imageImportBusy;

    async void ImportImage_Click(object sender,RoutedEventArgs e)
    {
        if(_imageImportBusy)
        {
            StatusText.Text="图片正在导入，请等待校验完成。";
            return;
        }
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
        if(picker.ShowDialog(Window.GetWindow(this))!=true)return;
        var scriptAtStart=_scriptId;
        var originalAttachment=row.Attachment;
        _imageImportBusy=true;
        try
        {
            StatusText.Text="正在导入并检查图片完整性…";
            var data=ReadData<ImageAttachmentInfo>(await MainWindow.SendAsync(
                ControlCommands.ImageImport,25000,
                new ImageImportRequest(picker.FileName)));

            // The backend's hash verification is the authority. Never update
            // the draft to a new reference before verifying that it can be read.
            var verified=ReadData<ImageAttachmentInfo>(await MainWindow.SendAsync(
                ControlCommands.ImageLookup,15000,
                new ImageLookupRequest(data.Reference)));
            if(!string.Equals(verified.Reference,data.Reference,StringComparison.Ordinal))
                throw new IOException("导入图片后校验引用不一致。");

            // A user can switch scripts while the IPC requests are running.
            // Do not apply the result to an obsolete row or overwrite an edit.
            if(!Equals(scriptAtStart,_scriptId) || !_steps.Contains(row) ||
               row.Attachment!=originalAttachment)
            {
                StatusText.Text="图片已导入本地，但当前消息已经发生变化。没有覆盖原附件，请重新选择消息添加。";
                return;
            }
            row.Attachment=verified.Reference;
            row.SetImageHealth("ok","导入后校验通过");
            _dirty=true;
            StatusText.Text=$"图片「{data.OriginalName}」导入并校验成功。请保存剧本。";
        }
        catch(Exception ex)
        {
            // The original reference and its previous health status are
            // intentionally unchanged when importing the replacement fails.
            StatusText.Text=$"图片导入或校验失败，原附件未改变：{ex.Message}";
            MessageBox.Show(Window.GetWindow(this),ex.Message,"图片导入失败",
                MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally { _imageImportBusy=false; }
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
        var currentScript=_scriptId;
        var generation=_scriptLoadGeneration;
        var originalReference=row.Attachment;
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
                if(generation!=_scriptLoadGeneration || currentScript!=_scriptId ||
                   !_steps.Contains(row) || row.Attachment!=originalReference)
                {
                    StatusText.Text="图片迁移期间编辑内容已变化，没有覆盖新附件。";
                    return;
                }
                row.Attachment=migrated.Reference;
                _dirty=true;
                StatusText.Text="已恢复旧版图片，请保存剧本以保留新的附件引用。";
            }
            var expectedReference=row.Attachment;
            var info=ReadData<ImageAttachmentInfo>(await MainWindow.SendAsync(
                ControlCommands.ImageLookup,15000,
                new ImageLookupRequest(expectedReference)));
            if(generation!=_scriptLoadGeneration || currentScript!=_scriptId ||
               !_steps.Contains(row) || row.Attachment!=expectedReference ||
               info.Reference!=expectedReference)
            {
                StatusText.Text="预览期间选中内容已变化，请重新打开图片。";
                return;
            }
            var bitmap=new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption=BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth=1400;
            bitmap.UriSource=new Uri(info.AbsolutePath,UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            // Keep the preview dialog inside the visible desktop work area
            // on smaller displays and high-DPI Windows installations.
            var workArea=SystemParameters.WorkArea;
            var previewWidth=Math.Min(850,Math.Max(320,workArea.Width-70));
            var previewHeight=Math.Min(670,Math.Max(270,workArea.Height-70));
            var window=new Window
            {
                Owner=Window.GetWindow(this),
                Title=$"预览图片 - {info.OriginalName}",
                Width=previewWidth,Height=previewHeight,
                MinWidth=Math.Min(500,previewWidth),
                MinHeight=Math.Min(380,previewHeight),
                WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Background=Brushes.Black,
                Content=new Border
                {
                    Padding=new Thickness(15),
                    Child=new Image{Source=bitmap,Stretch=Stretch.Uniform}
                }
            };
            row.SetImageHealth("ok","附件校验通过");
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

    bool _saveInProgress;
    async void Save_Click(object sender,RoutedEventArgs e)
    {
        if(_saveInProgress)return;
        _saveInProgress=true;
        try
        {
            var request=CollectDraft();
            var originalScriptId=_scriptId;
            var signature=ComputeDraftSignature();
            var generation=_scriptLoadGeneration;
            var raw=await MainWindow.SendAsync(ControlCommands.ScriptSave,16000,request);
            var doc=ReadData<ScriptEditorDocument>(raw);
            if(generation!=_scriptLoadGeneration || _scriptId!=originalScriptId ||
               signature!=ComputeDraftSignature())
            {
                // The server saved the submitted snapshot. Do not discard any
                // edits made after submission or switch the current editor.
                if(generation==_scriptLoadGeneration && _scriptId==originalScriptId)
                {
                    _scriptId=doc.ScriptId;
                    _revision=doc.Revision;
                    _dirty=true;
                    SaveLocalDraft();
                }
                StatusText.Text="提交时的剧本版本已保存，但编辑区有后续修改；已保留当前内容，请再次保存。";
                return;
            }
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
        finally{_saveInProgress=false;}
    }

    void Import_Click(object sender,RoutedEventArgs e)
    {
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
            if(string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Length>120 ||
               draft.Steps is null || draft.Steps.Count==0 || draft.Steps.Count>1500)
                throw new InvalidDataException("剧本名称或消息数量无效（最多 1500 条）。");
            // A cancelled picker or invalid file must not discard the current
            // editor. Only ask to replace it after import validation succeeds.
            if(!ConfirmDiscard())return;
            ++_scriptLoadGeneration;
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

    bool _bundleBusy;

    async void ExportBundle_Click(object sender,RoutedEventArgs e)
    {
        if(_bundleBusy)return;
        if(_dirty && (_savedSignature is null ||
           ComputeDraftSignature()!=_savedSignature))
        {
            if(MessageBox.Show(Window.GetWindow(this),
                "批量备份只包含正式保存的剧本，不包含当前未保存的编辑内容。是否继续？",
                "确认批量备份范围",MessageBoxButton.YesNo,
                MessageBoxImage.Information)!=MessageBoxResult.Yes)return;
        }
        var picker=new SaveFileDialog
        {
            Title="备份全部已保存剧本及图片",
            FileName="Signal-剧本图片完整备份.zip",
            Filter="ZIP 备份 (*.zip)|*.zip",
            AddExtension=true
        };
        if(picker.ShowDialog(Window.GetWindow(this))!=true)return;
        _bundleBusy=true;
        try
        {
            StatusText.Text="正在读取已保存剧本及验证附件…";
            var catalog=ReadData<List<ScriptEditorSummary>>(
                await MainWindow.SendAsync(ControlCommands.ScriptList,10000));
            if(catalog.Count is <1 or >ScriptBundleArchive.MaxScripts)
                throw new InvalidDataException("剧本数量超出完整备份支持范围（1～500）。");
            var scripts=new List<ScriptSaveRequest>();
            foreach(var item in catalog)
            {
                var doc=ReadData<ScriptEditorDocument>(
                    await MainWindow.SendAsync(ControlCommands.ScriptRead,10000,
                        new ScriptReadRequest(item.ScriptId)));
                scripts.Add(new ScriptSaveRequest(null,doc.Name,doc.TargetGroupId,
                    0,doc.Steps));
            }
            var imagePaths=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var reference in scripts.SelectMany(x=>x.Steps)
                .Select(x=>x.Attachment)
                .Where(x=>!string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal))
            {
                if(reference.StartsWith("img:",StringComparison.Ordinal))
                {
                    var info=ReadData<ImageAttachmentInfo>(
                        await MainWindow.SendAsync(ControlCommands.ImageLookup,
                            15000,new ImageLookupRequest(reference)));
                    if(info.Reference!=reference)
                        throw new IOException("图片引用校验不一致，备份已停止。");
                    imagePaths[reference]=info.AbsolutePath;
                }
                else
                {
                    // Legacy V7 paths must exist locally to make this backup
                    // portable. Never silently omit a missing attachment.
                    if(!File.Exists(reference))
                        throw new FileNotFoundException(
                            "旧版图片已缺失，请先修复或移除后重试备份。",reference);
                    imagePaths[reference]=reference;
                }
            }
            ScriptBundleArchive.Create(picker.FileName,scripts,imagePaths);
            StatusText.Text=$"完整备份成功：{scripts.Count} 个剧本、{imagePaths.Count} 张图片。";
        }
        catch(Exception ex)
        {
            StatusText.Text="完整备份失败："+ex.Message;
            MessageBox.Show(Window.GetWindow(this),ex.Message,
                "无法创建完整备份",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally{_bundleBusy=false;}
    }

    async void ImportBundle_Click(object sender,RoutedEventArgs e)
    {
        if(_bundleBusy)return;
        var picker=new OpenFileDialog
        {
            Title="从 ZIP 恢复剧本及图片",
            Filter="ZIP 备份 (*.zip)|*.zip",
            CheckFileExists=true
        };
        if(picker.ShowDialog(Window.GetWindow(this))!=true)return;
        _bundleBusy=true;
        var staging=Path.Combine(Path.GetTempPath(),
            "ms002-bundle-"+Guid.NewGuid().ToString("N"));
        var completed=0;
        try
        {
            StatusText.Text="正在检查完整备份及图片哈希…";
            var data=ScriptBundleArchive.ExtractValidated(picker.FileName,staging);
            var answer=MessageBox.Show(Window.GetWindow(this),
                $"备份包含 {data.Scripts.Length} 个剧本、{data.ImportedImagePaths.Count} 张图片。"+
                "\n将作为新剧本导入，不覆盖现有剧本。原账号和群组仍需在本机检查。确定恢复？",
                "确认完整备份恢复",MessageBoxButton.YesNo,MessageBoxImage.Warning);
            if(answer!=MessageBoxResult.Yes)return;

            var imported=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var item in data.ImportedImagePaths)
            {
                var info=ReadData<ImageAttachmentInfo>(
                    await MainWindow.SendAsync(ControlCommands.ImageImport,30000,
                        new ImageImportRequest(item.Value)));
                if(!info.Reference.StartsWith("img:",StringComparison.Ordinal))
                    throw new IOException("图片导入未返回有效的引用。");
                imported[item.Key]=info.Reference;
            }
            foreach(var script in data.Scripts)
            {
                var steps=script.Steps.Select((step,i)=>step with
                {
                    Position=i,
                    Attachment=string.IsNullOrWhiteSpace(step.Attachment)
                        ?""
                        :imported[step.Attachment]
                }).ToArray();
                // Account IDs and group IDs may differ on another installation.
                // Keep script content and author choices; operator must review.
                var request=new ScriptSaveRequest(null,script.Name,
                    script.TargetGroupId,0,steps);
                ReadData<ScriptEditorDocument>(await MainWindow.SendAsync(
                    ControlCommands.ScriptSave,30000,request));
                completed++;
                StatusText.Text=$"正在恢复剧本：{completed}/{data.Scripts.Length}";
            }
            await ReloadScriptsAsync();
            StatusText.Text=$"已恢复 {completed} 个新剧本及 {imported.Count} 张图片。" +
                " 请核对本机账号、群组及附件后再运行。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"恢复失败：{ex.Message}。"+
                $"已成功导入 {completed} 个剧本；可在剧本库查看，避免再次重复导入。";
            MessageBox.Show(Window.GetWindow(this),StatusText.Text,
                "备份恢复未完全成功",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally
        {
            _bundleBusy=false;
            try{if(Directory.Exists(staging))Directory.Delete(staging,true);}
            catch(IOException){ }
            catch(UnauthorizedAccessException){ }
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
    string _attachmentHealth="";
    string _attachmentHealthDetail="";
    public string AttachmentLabel=>!HasAttachment?"添加图片":
        _attachmentHealth switch
        {
            "ok"=>"预览图片",
            "missing"=>"图片缺失",
            "invalid"=>"图片损坏",
            "legacy"=>"旧版图片",
            _=>"预览图片"
        };
    public bool HasAttachment=>!string.IsNullOrWhiteSpace(Attachment);
    public bool IsAttachmentBroken=>HasAttachment &&
        (_attachmentHealth=="missing" || _attachmentHealth=="invalid");
    public string AttachmentHint=>!HasAttachment
        ?"点击选择要添加的图片；右键可添加或替换图片。"
        :string.IsNullOrWhiteSpace(_attachmentHealthDetail)
            ?"左键预览图片，右键可替换或移除。"
            :_attachmentHealthDetail+"；右键可替换或移除。";
    public void SetImageHealth(string status,string detail)
    {
        _attachmentHealth=status;
        _attachmentHealthDetail=detail;
        PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(AttachmentLabel)));
        PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(AttachmentHint)));
        PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(IsAttachmentBroken)));
    }
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
            if(_attachment==value)return;
            Set(ref _attachment,value??"");
            _attachmentHealth="";
            _attachmentHealthDetail="";
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(AttachmentLabel)));
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(AttachmentHint)));
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(HasAttachment)));
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(IsAttachmentBroken)));
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
