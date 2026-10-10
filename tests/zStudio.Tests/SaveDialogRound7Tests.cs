using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// A source project's save published off the thread that edits it: the files are written on the thread pool while the
/// workspace refuses every change, the saved state and its notification come back on the editing thread's context, and a
/// save canceled before publication or failing in it leaves the edits unsaved and the workspace editable.
/// </summary>
public sealed class SaveDialogRound7Tests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Script = "gamegen/m1.gs", Pickups = "data/m1/zrdr/puppies.zrd";
    private readonly string root = Path.Combine(Path.GetTempPath(), "zstudio-save-round7-" + Guid.NewGuid().ToString("N"));

    public SaveDialogRound7Tests()
    {
        Write(Script, "set worldName world\r\nQuit\r\n");
        Write(Pickups, "( )\n");
    }
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
    private string At(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private void Write(string relative, string text) { Directory.CreateDirectory(Path.GetDirectoryName(At(relative))!); File.WriteAllText(At(relative), text, Encoding.Latin1); }
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);
    private static IReadOnlyList<string> Publish(string project, IReadOnlyList<(string Relative, byte[]? Expected, byte[]? Content)> writes, string description, CancellationToken token) =>
        new SourcePublisher(project).Publish([.. writes.Select(w => new SourceFileWrite(w.Relative, w.Expected, w.Content))], description, token).Written;

    [Fact]
    public void SaveAsyncPublishesOffTheEditingThreadAndRefusesChangesUntilItEnds()
    {
        using SemaphoreSlim entered = new(0), proceed = new(0);
        int? publisherThread = null;
        SourceWorkspace workspace = new(root, (writes, description, token) =>
        {
            publisherThread = Environment.CurrentManagedThreadId; entered.Release();
            // A save that blocked its caller never lets the test release it: fail instead of hanging.
            if (!proceed.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException("The save blocked the thread that started it.");
            return Publish(root, writes, description, token);
        });
        List<(string Kind, int Thread)> changes = [];
        workspace.Changed += c => changes.Add((c.Kind, Environment.CurrentManagedThreadId));
        workspace.Apply("Edit script", [(Script, Bytes("set worldName world2\r\nQuit\r\n"))], Token);
        workspace.Apply("Edit pickups", [(Pickups, Bytes("( ( NANITE 1 ( 1.0 2.0 3.0 ) ( 0.0 0.0 0.0 ) 30.0 ) )\n"))], Token);

        using EditingThread editing = new();
        IReadOnlyList<string>? written = null;
        editing.Run(async () =>
        {
            var saving = workspace.SaveAsync(Token);
            // The caller gets the save back while the files are being written.
            Assert.False(saving.IsCompleted);
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.NotEqual(editing.ThreadId, publisherThread);
            Assert.True(workspace.IsSaving); Assert.True(workspace.IsDirty);
            Assert.False(workspace.CanUndo); Assert.False(workspace.CanRedo);
            // Every change waits for the save: it would be lost or overwrite what is being written.
            Assert.Throws<InvalidOperationException>(() => workspace.Apply("Late edit", [(Script, Bytes("late\r\n"))], Token));
            Assert.Throws<InvalidOperationException>(() => workspace.Discard());
            Assert.Throws<InvalidOperationException>(() => workspace.Reload());
            Assert.Throws<InvalidOperationException>(() => workspace.Save(Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.SaveAsync(Token));
            Assert.DoesNotContain(changes, c => c.Kind == "save");
            proceed.Release();
            written = await saving;
            // The saved state and its notification come back on the editing thread.
            Assert.Equal(editing.ThreadId, Environment.CurrentManagedThreadId);
            Assert.False(workspace.IsSaving); Assert.False(workspace.IsDirty);
            workspace.Apply("After the save", [(Script, Bytes("set worldName world3\r\nQuit\r\n"))], Token);
        });
        Assert.Equal([Pickups, Script], written!.Order(StringComparer.Ordinal));
        Assert.Equal(("save", editing.ThreadId), changes.Single(c => c.Kind == "save"));
        Assert.Equal("set worldName world2\r\nQuit\r\n", File.ReadAllText(At(Script), Encoding.Latin1));
        Assert.Empty(new SourcePublisher(root).FindInterrupted(Token));
        Assert.Equal([Script], workspace.DirtyFiles);
    }

    [Fact]
    public async Task SaveAsyncCanceledOrFailedKeepsTheEditsUnsavedAndEditable()
    {
        bool fail = false;
        SourceWorkspace workspace = new(root, (writes, description, token) => fail ? throw new IOException("The disk is full.") : Publish(root, writes, description, token));
        workspace.Apply("Edit script", [(Script, Bytes("edited\r\n"))], Token);

        // Canceled before publication: nothing is written.
        using (CancellationTokenSource canceled = new())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.SaveAsync(canceled.Token));
        }
        Assert.False(workspace.IsSaving); Assert.Equal([Script], workspace.DirtyFiles);
        Assert.Equal("set worldName world\r\nQuit\r\n", File.ReadAllText(At(Script), Encoding.Latin1));

        // A publication that fails leaves the edits unsaved, and the workspace takes the next edit and save.
        fail = true;
        Assert.Equal("The disk is full.", (await Assert.ThrowsAsync<IOException>(() => workspace.SaveAsync(Token))).Message);
        Assert.False(workspace.IsSaving); Assert.True(workspace.CanUndo); Assert.Equal([Script], workspace.DirtyFiles);
        workspace.Apply("Edit again", [(Script, Bytes("edited again\r\n"))], Token);
        fail = false;
        Assert.Equal([Script], await workspace.SaveAsync(Token));
        Assert.False(workspace.IsDirty);
        Assert.Equal("edited again\r\n", File.ReadAllText(At(Script), Encoding.Latin1));
        // Nothing dirty: nothing to publish.
        Assert.Empty(await workspace.SaveAsync(Token));
    }

    /// <summary>A thread with a single-threaded synchronization context, as the UI dispatcher gives the workspace's edits.</summary>
    private sealed class EditingThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
        public int ThreadId { get; private set; }
        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
        /// <summary>Runs <paramref name="work"/> and its continuations on a new thread until it completes; rethrows its failure.</summary>
        public void Run(Func<Task> work)
        {
            Exception? failure = null;
            Thread thread = new(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                SetSynchronizationContext(this);
                Task task;
                try { task = work(); }
                catch (Exception ex) { failure = ex; return; }
                task.ContinueWith(_ => queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var (callback, state) in queue.GetConsumingEnumerable()) callback(state);
                if (task.Exception is { } error) failure = error.InnerException ?? error;
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The editing thread did not finish.");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
        public void Dispose() => queue.Dispose();
    }
}
