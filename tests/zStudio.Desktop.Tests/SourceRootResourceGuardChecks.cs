using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>A project at a filesystem root still owns its dirty source files against direct resource editing.</summary>
internal static partial class SourceRootResourceGuardChecks
{
    internal static async Task Run()
    {
        using SourceWorldFixture fixture = new();
        const string relative = "data/m1/zrdr/guard.zrd";
        fixture.Write(relative, "( 1 )\n");
        using TestAlias alias = TestAlias.Create(fixture.Project);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await main.ViewModel.OpenRootAsync(alias.Root, token);
            var workspace = (SourceWorkspace)typeof(MainWindow).GetMethod("SourceWorkspaceFor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [alias.Root])!;
            var doc = (await main.ViewModel.OpenFileAsync(Path.Combine(alias.Root, relative), token))!;
            var edits = doc.ResourceEdits!;
            var member = Assert.Single(edits.Current.Members);
            // Text files list the implicit root array's children; these parentheses are a nested array.
            var array = Assert.Single(edits.Tree(member, token).Children);
            Assert.Equal(ZrdKind.Array, array.Kind);
            var node = Assert.Single(array.Children);
            Assert.Equal(ZrdKind.Int, node.Kind); Assert.Equal(1u, node.Bits);
            var prepared = await edits.PrepareZrdAsync(member.Id, node.Id, "set", value: "3", token: token);
            byte[] original = File.ReadAllBytes(fixture.Path(relative));
            workspace.Apply("Source edit", [(relative, Encoding.ASCII.GetBytes("( 2 )\n"))], token);
            long revision = workspace.Revision, documentRevision = doc.Revision;
            var error = Assert.Throws<InvalidOperationException>(() => edits.Accept(prepared));
            Assert.Contains("source project holds unsaved edits", error.Message);
            Assert.False(edits.IsDirty); Assert.False(edits.HasHistory);
            Assert.Equal(documentRevision, doc.Revision); Assert.Equal(revision, workspace.Revision);
            Assert.Equal("( 2 )\n", Encoding.ASCII.GetString(workspace.Read(relative, token)!));
            Assert.Equal(original, File.ReadAllBytes(fixture.Path(relative)));

            // Removing the conflict permits the very same prepared resource edit; the reverse order is guarded too.
            workspace.Undo();
            edits.Accept(prepared);
            Assert.True(edits.IsDirty);
            Assert.Throws<InvalidDataException>(() => workspace.Apply("Conflicting source edit", [(relative, Encoding.ASCII.GetBytes("( 4 )\n"))], token));
            var exportRefusal = await Assert.ThrowsAsync<StudioCommandException>(async () =>
            { await main.ExportSourceProjectAsync(null, ["m1/gamez.zbd"], false, token); });
            Assert.Equal("unsaved_changes", exportRefusal.Code);
            Assert.Contains("guard.zrd", exportRefusal.Message);
            Assert.Equal(original, File.ReadAllBytes(fixture.Path(relative)));
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
        await StaleCheckoutPreservesReplacement(token);
    }

    private static async Task StaleCheckoutPreservesReplacement(CancellationToken token)
    {
        using SourceWorldFixture original = new(), replacement = new();
        using TestAlias alias = TestAlias.Create(original.Project);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        using var proceed = new SemaphoreSlim(0);
        TaskCompletionSource<BlenderCheckout> written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await main.ViewModel.OpenRootAsync(alias.Root, token);
            var workspace = (SourceWorkspace)typeof(MainWindow).GetMethod("SourceWorkspaceFor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [alias.Root])!;
            main.CheckoutSourceModel = (w, model, ct) =>
            {
                try
                {
                    var checkout = SourceBlender.Checkout(w, model, ct);
                    written.TrySetResult(checkout);
                    Assert.True(proceed.Wait(TimeSpan.FromSeconds(20), ct));
                    return checkout;
                }
                catch (Exception ex) { written.TrySetException(ex); throw; }
            };
            var pending = (Task<BlenderCheckout>)typeof(MainWindow).GetMethod("CheckoutForBlenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [original.Tank, token])!;
            var checkout = await written.Task.WaitAsync(token);
            string relative = Path.GetRelativePath(alias.Root, checkout.Folder);
            string actualCheckout = Path.Combine(original.Project, relative);
            byte[] manifest = File.ReadAllBytes(Path.Combine(actualCheckout, "manifest.json"));
            workspace.Apply("Change while checkout finishes", [("gamegen/m1.gs", Encoding.ASCII.GetBytes("Quit\n"))], token);
            string foreignFolder = Path.Combine(replacement.Project, relative);
            Directory.CreateDirectory(foreignFolder);
            string sentinel = Path.Combine(foreignFolder, "unrelated.txt");
            File.WriteAllText(sentinel, "external replacement survives");
            alias.Remap(replacement.Project);
            proceed.Release();
            var refusal = await Assert.ThrowsAsync<StudioCommandException>(async () => { await pending; });
            Assert.Equal("context_changed", refusal.Code);
            Assert.Contains("checkout completed", refusal.Message);
            Assert.Contains("recorded path", refusal.Message);
            Assert.Equal("external replacement survives", File.ReadAllText(sentinel));
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(actualCheckout, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(actualCheckout, "input", Path.GetFileName(original.Tank))));
        }
        finally { proceed.Release(); main.Close(); }
    }

    // Uses an unused per-session DOS drive alias; cleanup removes only this fixture's exact mapping.
    private sealed partial class TestAlias(string drive, string target) : IDisposable
    {
        private string? currentTarget = target;
        public string Root => drive + @"\";
        public static TestAlias Create(string directory)
        {
            char[] names = new char[32768];
            for (char letter = 'Z'; letter >= 'D'; letter--)
            {
                string drive = letter + ":";
                if (QueryDosDevice(drive, names, (uint)names.Length) != 0) continue;
                int error = Marshal.GetLastPInvokeError();
                if (error != 2) throw new Win32Exception(error);
                string target = @"\??\" + directory;
                if (!DefineDosDevice(0x1 | 0x8, drive, target)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                return new(drive, target);
            }
            throw new IOException("No unused drive letter is available for the temporary source-root fixture.");
        }
        public void Dispose()
        {
            if (currentTarget == null) return;
            if (!DefineDosDevice(0x1 | 0x2 | 0x4 | 0x8, drive, currentTarget)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            currentTarget = null;
        }
        public void Remap(string directory)
        {
            Dispose();
            string next = @"\??\" + directory;
            if (!DefineDosDevice(0x1 | 0x8, drive, next)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            currentTarget = next;
        }
        [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string device, [Out] char[] buffer, uint length);
        [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DefineDosDevice(uint flags, string device, string target);
    }
}
