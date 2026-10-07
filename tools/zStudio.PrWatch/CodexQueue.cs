using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.PrWatch;

public interface INoticeQueue
{
    Task CheckAsync(Guid thread, CancellationToken token);
    Task<string> AddAsync(Guid thread, Notice notice, CancellationToken token);
    Task<string> RemoveAsync(Guid thread, Notice notice, CancellationToken token);
}

public interface IQueueRpc : IAsyncDisposable
{
    Task<JsonObject> CallAsync(string method, JsonObject args);
    Task<JsonObject[]> ListAsync(Guid thread);
}
public sealed class CodexQueue(string executable, string workspace, Func<CancellationToken, Task<IQueueRpc>>? connect = null) : INoticeQueue
{
    private async Task<IQueueRpc> OpenAsync(CancellationToken token) => connect != null ? await connect(token) : await Rpc.OpenAsync(executable, workspace, token);
    public async Task CheckAsync(Guid thread, CancellationToken token)
    {
        await using var rpc = await OpenAsync(token);
        await rpc.ListAsync(thread);
    }
    /// <summary>Inspect only this notice; a missing queue row is never proof of delivery.</summary>
    public async Task<string> InspectAsync(Guid thread, Notice notice, CancellationToken token)
    {
        await using var rpc = await OpenAsync(token);
        var rows = (await rpc.ListAsync(thread)).Where(r => r["clientUserMessageId"]?.GetValue<string>() == notice.Id.ToString("D")).ToArray();
        if (rows.Length == 0) return "absent (not delivery proof)";
        if (rows.Length != 1 || !JsonNode.DeepEquals(rows[0]["input"], Input(notice.Message)) ||
            (notice.Submission != null && rows[0]["id"]?.GetValue<string>() != notice.Submission))
            throw new IOException("Queue identity/content changed; delivery cannot be established.");
        return "pending in Codex queue (not yet acknowledged; an active turn can delay delivery)";
    }
    public async Task<string> AddAsync(Guid thread, Notice notice, CancellationToken token)
    {
        await using var rpc = await OpenAsync(token);
        var input = Input(notice.Message);
        var result = await rpc.CallAsync("thread/queue/add", new JsonObject
        { ["threadId"] = thread.ToString("D"), ["clientUserMessageId"] = notice.Id.ToString("D"), ["input"] = input.DeepClone() });
        var submission = result["queuedSubmission"] ?? throw new IOException("Queue add omitted its receipt; delivery is uncertain.");
        if (submission["clientUserMessageId"]?.GetValue<string>() != notice.Id.ToString("D") || !JsonNode.DeepEquals(submission["input"], input) ||
            submission["id"]?.GetValue<string>() is not { Length: > 0 } id) throw new IOException("Queue add returned an inconsistent receipt; do not resend.");
        return id;
    }
    public async Task<string> RemoveAsync(Guid thread, Notice notice, CancellationToken token)
    {
        await using var rpc = await OpenAsync(token);
        var rows = await rpc.ListAsync(thread);
        var matching = rows.Where(r => r["clientUserMessageId"]?.GetValue<string>() == notice.Id.ToString("D")).ToArray();
        if (matching.Length == 0) return "absent (not delivery proof)";
        if (matching.Length != 1 || !JsonNode.DeepEquals(matching[0]["input"], Input(notice.Message)) ||
            (notice.Submission != null && matching[0]["id"]?.GetValue<string>() != notice.Submission)) throw new IOException("Queue identity/content changed; no message removed.");
        var result = await rpc.CallAsync("thread/queue/delete", new JsonObject { ["threadId"] = thread.ToString("D"), ["queuedSubmissionId"] = matching[0]["id"]!.GetValue<string>() });
        if (result["deleted"] is not JsonValue deleted || !deleted.TryGetValue<bool>(out bool removed)) throw new IOException("Queue deletion result is uncertain.");
        return removed ? "removed" : "already absent";
    }
    private static JsonArray Input(string message) => [new JsonObject { ["type"] = "text", ["text"] = message, ["text_elements"] = new JsonArray() }];

    private sealed class Rpc : IQueueRpc
    {
        private readonly Process process;
        private readonly CancellationTokenSource deadline;
        private readonly Task stderr;
        private int sequence;
        private Rpc(Process process, CancellationToken token)
        {
            this.process = process; deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30)); stderr = DrainErrorsAsync();
        }
        private async Task DrainErrorsAsync()
        {
            // Consume bounded chunks without retaining potentially sensitive diagnostic text.
            char[] buffer = new char[2048];
            try { while (await process.StandardError.ReadAsync(buffer.AsMemory(), deadline.Token) != 0) { } }
            catch (OperationCanceledException) { }
        }
        public static async Task<Rpc> OpenAsync(string executable, string workspace, CancellationToken token)
        {
            var info = CommandRunner.StartInfo(executable, ["app-server", "--stdio"], workspace);
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            Rpc rpc = new(Process.Start(info) ?? throw new IOException("Could not start the queue transport."), token);
            try
            {
                await rpc.CallAsync("initialize", new JsonObject
                { ["clientInfo"] = new JsonObject { ["name"] = "zstudio_pr_watch", ["version"] = "1" }, ["capabilities"] = new JsonObject { ["experimentalApi"] = true } });
                await rpc.SendAsync(new JsonObject { ["method"] = "initialized" }); return rpc;
            }
            catch { await rpc.DisposeAsync(); throw; }
        }
        private async Task SendAsync(JsonObject value)
        {
            await process.StandardInput.WriteLineAsync(value.ToJsonString().AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
        }
        public async Task<JsonObject> CallAsync(string method, JsonObject args)
        {
            int id = ++sequence;
            await SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args });
            // ReadLineAsync itself has no length limit; bound before building/parsing a line.
            StringBuilder line = new(); char[] one = new char[1]; int notices = 0;
            while (await process.StandardOutput.ReadAsync(one.AsMemory(), deadline.Token) != 0)
            {
                if (one[0] != '\n') { if (line.Length >= 1024 * 1024) throw new IOException("Queue response exceeds 1 MiB."); line.Append(one[0]); continue; }
                var response = JsonNode.Parse(line.ToString())?.AsObject() ?? throw new IOException("Invalid queue response."); line.Clear();
                if (response["id"] == null) { if (++notices > 1000) throw new IOException("Too many unsolicited queue messages."); continue; }
                if (response["id"]!.GetValue<int>() != id) throw new IOException("Unexpected queue response identity.");
                if (response["error"] != null) throw new IOException("Codex rejected " + method + ": " + response["error"]!.ToJsonString());
                return response["result"]?.AsObject() ?? throw new IOException("Queue response omitted its result.");
            }
            throw new IOException("Queue transport ended; delivery may be uncertain.");
        }
        public async Task<JsonObject[]> ListAsync(Guid thread)
        {
            List<JsonObject> rows = []; HashSet<string> cursors = [], ids = []; string? cursor = null;
            for (int page = 0; page < 100; page++)
            {
                var result = await CallAsync("thread/queue/list", new JsonObject { ["threadId"] = thread.ToString("D"), ["limit"] = 100, ["cursor"] = cursor });
                var data = result["data"]?.AsArray() ?? throw new IOException("Queue list omitted data.");
                if (data.Count > 100) throw new IOException("Invalid queue page length.");
                foreach (var item in data)
                {
                    if (item is not JsonObject row || row["id"]?.GetValue<string>() is not { Length: > 0 } id || !ids.Add(id) ||
                        row["input"] is not JsonArray || row["clientUserMessageId"]?.GetValue<string>() is not { Length: > 0 })
                        throw new IOException("Queue list returned ambiguous identities.");
                    rows.Add(row);
                }
                cursor = result["nextCursor"]?.GetValue<string>();
                if (cursor == null) return rows.ToArray();
                if (cursor.Length == 0 || !cursors.Add(cursor)) throw new IOException("Invalid queue cursor.");
            }
            throw new IOException("Queue pagination limit reached.");
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(CancellationToken.None); }
            }
            finally { deadline.Cancel(); await stderr; deadline.Dispose(); process.Dispose(); }
        }
    }
}
