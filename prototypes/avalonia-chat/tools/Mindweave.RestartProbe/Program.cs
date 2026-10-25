using System.Text.Json;
using Mindweave.Core;

if (args.Length != 2 || args[0] is not ("write" or "read"))
{
    Console.Error.WriteLine("Usage: Mindweave.RestartProbe write|read /absolute/path/state.json");
    return 2;
}
var store = new JsonWorkspaceStore(args[1]);
if (args[0] == "write")
{
    var state = DemoContent.Create();
    var message = state.Current.Messages.Single(x => x.Id == "rail-answer");
    var block = MarkdownDocument.Parse(message.Markdown).First(x => x.Text.StartsWith("19 世纪初"));
    var start = block.Text.IndexOf("蒸汽机", StringComparison.Ordinal);
    state.Branch(message.Id, block.Id, start, start + 3, new("rail-answer/" + block.Id, -11, 7), "root process draft");
    state.Current.Draft = "process draft";
    state.Current.Position = new("probe-partial/b0", -9, 4);
    state.Current.Messages.Add(new("probe-partial", false, "收到的分块内容", ReplyStatus.Generating));
    await store.SaveAsync(state);
    Console.WriteLine(JsonSerializer.Serialize(new { processId = Environment.ProcessId, currentId = state.Current.Id, file = store.FilePath }));
}
else
{
    var state = await store.LoadAsync() ?? throw new InvalidDataException("No saved workspace.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        processId = Environment.ProcessId,
        currentId = state.Current.Id,
        quote = state.Current.Source?.Quote,
        parentId = state.Current.Source?.ConversationId,
        draft = state.Current.Draft,
        character = state.Current.Position.CharacterIndex,
        partial = state.Current.Messages.Last().Markdown,
        status = state.Current.Messages.Last().Status.ToString(),
        conversationCount = state.Conversations.Count
    }));
}
return 0;
