using System.IO;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class AsyncFieldDraftTests
{
    [Fact]
    public async Task OverlappingCommitSharesWorkAndRetainsNewerInput()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously); List<string> accepted = [];
        FieldDraft draft = new("old", _ => throw new Exception("Synchronous callback must not run"), async value => { await gate.Task; accepted.Add(value); });
        draft.Text = "first"; var first = draft.CommitAsync(); var second = draft.CommitAsync(); Assert.Same(first, second); Assert.True(draft.IsCommitting);
        draft.Text = "newer"; gate.SetResult(); Assert.True(await first); Assert.Equal("first", draft.Committed); Assert.Equal("newer", draft.Text); Assert.True(draft.IsPending);
        Assert.True(await draft.CommitAsync()); Assert.Equal(new[] { "first", "newer" }, accepted); Assert.False(draft.IsPending);
    }
    [Fact]
    public async Task FailedAsyncValidationRetainsInputAndCanRetry()
    {
        bool fail = true;
        FieldDraft draft = new("old", _ => { }, value => fail ? Task.FromException(new InvalidDataException("Bad value")) : Task.CompletedTask);
        draft.Text = "input"; Assert.False(await draft.CommitAsync()); Assert.Equal("Bad value", draft.Error); Assert.Equal("old", draft.Committed); Assert.Equal("input", draft.Text);
        fail = false; Assert.True(await draft.CommitAsync()); Assert.Null(draft.Error); Assert.Equal("input", draft.Committed);
    }
}
