using System.ComponentModel;
using Mindweave.Core;

namespace Mindweave.Desktop;

public sealed class WorkspaceViewModel : INotifyPropertyChanged
{
    public static async Task<WorkspaceViewModel> OpenSavedWorkspaceAsync(string filePath)
    {
        var store = new JsonWorkspaceStore(filePath);
        try { return new(await store.LoadAsync() ?? DemoContent.Create(), store: store); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new(persistenceError: "无法读取保存文件 · 本次临时打开，原文件已保留", dataFileHint: store.FilePath);
        }
    }
    private readonly JsonWorkspaceStore? _store;
    private readonly string? _dataFileHint;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _dirty = true;
    public ExplorationState State { get; }
    public GenerationCoordinator Generation { get; }
    public Conversation Current => State.Current;
    public string? PersistenceError { get; private set; }
    public string? DataFile => _store?.FilePath ?? _dataFileHint;
    public bool CanSave => _store != null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<NavigationRequest>? Navigated;
    public event Action<GenerationUpdate>? MessageUpdated;

    public WorkspaceViewModel(ExplorationState? state = null, IReplyStream? replyStream = null, JsonWorkspaceStore? store = null, string? persistenceError = null, TimeSpan? demoDuration = null, string? dataFileHint = null)
    {
        State = state ?? DemoContent.Create();
        _store = store;
        _dataFileHint = dataFileHint;
        PersistenceError = persistenceError;
        Generation = new(State, replyStream ?? new FixedReplyStream(DemoReply, demoDuration));
        Generation.Updated += update =>
        {
            MarkDirty();
            PropertyChanged?.Invoke(this, new(nameof(Current)));
            MessageUpdated?.Invoke(update);
        };
    }

    public void SwitchTo(string id, ReadingPosition position, string draft) => Navigate(State.SwitchTo(id, position, draft));
    public void Branch(string messageId, string blockId, int start, int end, ReadingPosition position, string draft) => Navigate(State.Branch(messageId, blockId, start, end, position, draft));
    public void ReturnToSource(ReadingPosition position, string draft) => Navigate(State.ReturnToSource(position, draft));
    public void NewConversation(ReadingPosition position, string draft)
    {
        var conversation = new Conversation(Guid.NewGuid().ToString("N"), "新的探索");
        State.Conversations.Add(conversation);
        SwitchTo(conversation.Id, position, draft);
    }

    public void Send(string text) => _ = SendAsync(text);
    public Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        var current = Current;
        if (current.Title == "新的探索") current.Title = text.Trim().Length > 18 ? text.Trim()[..18] + "…" : text.Trim();
        return Generation.GenerateAsync(current.Id, text);
    }

    public void MarkDirty() => _dirty = true;
    public async Task<bool> SaveAsync()
    {
        if (_store == null) return true;
        await _saveGate.WaitAsync();
        try
        {
            if (!_dirty) return true;
            _dirty = false;
            try
            {
                await _store.SaveAsync(State);
                PersistenceError = null;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _dirty = true;
                PersistenceError = "保存失败，当前内容仍保留在窗口中。";
                return false;
            }
            finally { PropertyChanged?.Invoke(this, new(nameof(PersistenceError))); }
        }
        finally { _saveGate.Release(); }
    }

    private void Navigate(NavigationRequest request)
    {
        MarkDirty();
        PropertyChanged?.Invoke(this, new(nameof(Current)));
        Navigated?.Invoke(request);
    }

    private static string DemoReply(string text, string? quote)
    {
        if (text.Contains("四冲程")) return "# 四个动作，构成一个循环\n\n四冲程依次是**进气、压缩、做功、排气**。活塞上下运动两次，曲轴转过两圈，完成一次循环。\n\n1. **进气**：让空气或混合气进入气缸。\n2. **压缩**：活塞向上，把气体压缩。\n3. **做功**：燃烧产生的高压气体推动活塞。\n4. **排气**：把废气排出，准备下一轮。\n\n> 这是用于体验分支流程的固定示例回答。";
        if ((quote ?? text).Contains("蒸汽")) return "# 从热，到车轮的转动\n\n蒸汽机利用**蒸汽的压力差**推动活塞。锅炉先把水加热成蒸汽，阀门控制蒸汽进入气缸，连杆把活塞的往复运动变成车轮转动。\n\n## 可以分成三个动作\n\n1. 在锅炉中加热水，得到蒸汽。\n2. 蒸汽进入气缸，推动活塞。\n3. 连杆带动车轮，让运动持续下去。\n\n蒸汽机属于外燃机；**内燃机**则让燃料直接在发动机内部燃烧。你可以选中这里的文字，再开一个分支。\n\n> 这是固定示例回答，用于验证阅读与分支交互。";
        var context = quote == null
            ? "可以先选中这段示例回答中的文字，开启一条有来源的分支。"
            : "这条分支已经保留了来源，可以随时用右上角的**返回来源**回到原句。";
        var thirdStep = quote == null ? "在分支里发送问题，再回到原文继续阅读。" : "逐层返回，检查来源高亮与阅读位置。";
        return $"# 沿着这个问题继续\n\n你正在探索「{(quote ?? text).Replace("\n", " ")}」。{context}\n\n## 试试这条路径\n\n1. 阅读这段示例内容，并选中一个词或一句话。\n2. 点击**继续追问**，创建下一层分支。\n3. {thirdStep}\n\n侧栏中的普通切换会回到该会话最近读到的位置；已有分支入口可以再次进入，不会创建新分支。\n\n> 这是固定交互示例，没有调用 AI，也没有对你的问题进行真实推理。";
    }
}
