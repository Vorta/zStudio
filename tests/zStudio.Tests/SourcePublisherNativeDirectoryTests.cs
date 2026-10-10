using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    [Fact]
    public void CompletingAnInterruptedDeletionAcceptsItsAlreadyRemovedParent()
    {
        using Project project = new();
        string parent = project.Full("data/m1/new"); Directory.CreateDirectory(parent);
        File.WriteAllBytes(project.Full(Added), Text("original"));
        SourcePublisher publisher = Failing(project, "commit", -1, () => new SourcePublisher.Crash());
        Assert.Throws<SourcePublisher.Crash>(() => publisher.Publish([new(Added, Text("original"), null)], "Delete source", Token));
        Directory.Delete(parent);
        publisher.Fault = null;
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;

        SourceRecoveryResult result = publisher.Resolve(id, SourceRecoveryAction.Complete, Token);

        Assert.True(result.Resolved, string.Join("; ", result.Conflicts));
        Assert.Empty(result.Conflicts); Assert.Empty(result.Changed);
        Assert.False(Directory.Exists(parent)); Assert.False(File.Exists(project.Full(Added)));
        Assert.Empty(project.Leftovers()); Assert.Empty(publisher.FindInterrupted(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InPlaceDirectoryRedirectionCannotRedirectSourcePublication(bool recovering)
    {
        if (!OperatingSystem.IsWindows()) return;
        using Project project = new(), outside = new();
        string sentinel = outside.Full("added.zrd"); File.WriteAllText(sentinel, "outside sentinel");
        SourceFileWrite[] writes = [new(Added, null, Text("new source"))];
        SourcePublisher publisher = new(project.Root);
        string? id = null;
        if (recovering)
        {
            publisher.Fault = (step, _) => { if (step == "install") throw new SourcePublisher.Crash(); };
            Assert.Throws<SourcePublisher.Crash>(() => publisher.Publish(writes, "Interrupted new source", Token));
            publisher.Fault = null; id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        }
        InPlaceDirectoryJunction? mutation = null;
        publisher.Fault = (step, index) =>
        {
            if (step != (recovering ? "complete" : "install") || index != 0) return;
            // The directory is already held by publication. An attribute-only junction change still succeeds.
            mutation = new(project.Full("data/m1/new"), outside.Root);
        };
        try
        {
            if (recovering)
            {
                SourceRecoveryResult result = publisher.Resolve(id!, SourceRecoveryAction.Complete, Token);
                Assert.False(result.Resolved); Assert.NotEmpty(result.Conflicts);
            }
            else Assert.ThrowsAny<IOException>(() => publisher.Publish(writes, "New source", Token));
            Assert.NotNull(mutation);
            Assert.Equal("outside sentinel", File.ReadAllText(sentinel));
        }
        finally { mutation?.Dispose(); }
        Assert.False(File.Exists(project.Full(Added)));
        publisher.Fault = null;
        foreach (var pending in publisher.FindInterrupted(Token))
            Assert.True(publisher.Resolve(pending.SaveId, SourceRecoveryAction.RollBack, Token).Resolved);
    }
}

/// <summary>Test-only mutation of an empty temporary directory, preserving its identity and existing open handles.</summary>
internal sealed class InPlaceDirectoryJunction : IDisposable
{
    private readonly SafeFileHandle handle;
    internal InPlaceDirectoryJunction(string directory, string target)
    {
        handle = CreateFile(directory, 0x100, FileShare.ReadWrite | FileShare.Delete, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) { int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target)), printed = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] data = new byte[16 + substitute.Length + 2 + printed.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)printed.Length));
        substitute.CopyTo(data, 16); printed.CopyTo(data, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x900A4, data, data.Length, 0, 0, out _, 0))
        { int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
    }
    public void Dispose()
    {
        using (handle)
        {
            byte[] clear = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(clear, 0xA0000003);
            if (!DeviceIoControl(handle, 0x900AC, clear, clear.Length, 0, 0, out _, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, uint mode, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int length, nint output, int outputLength, out int returned, nint overlap);
}
