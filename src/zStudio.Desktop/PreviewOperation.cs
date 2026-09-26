namespace Recoil.Zbd.Desktop;

/// <summary>Flows MCP shutdown cancellation through preview work started by GUI bindings.
/// Only loading/seeking requests link this token; retained renderer/player lifetimes do not.</summary>
internal static class PreviewOperation
{
    private static readonly AsyncLocal<CancellationToken> current = new();
    internal static CancellationTokenSource Link(CancellationToken lifetime) =>
        CancellationTokenSource.CreateLinkedTokenSource(lifetime, current.Value);
    internal static IDisposable Begin(CancellationToken token) => new Scope(token);
    private sealed class Scope : IDisposable
    {
        private readonly CancellationToken previous = current.Value;
        private readonly CancellationTokenSource cancellation;
        internal Scope(CancellationToken token)
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            current.Value = cancellation.Token;
        }
        public void Dispose() { current.Value = previous; cancellation.Dispose(); }
    }
}
