namespace zStudio.Rendering;

/// <summary>Releases partially configured or attached preview resources before an explicit retry.</summary>
internal static class PreviewResourceLifetime
{
    internal static T Create<T>(Func<T> create, Action<T> configure) where T : class, IDisposable
    {
        T resource = create();
        try { configure(resource); return resource; }
        catch (Exception error)
        {
            if (Cleanup(error, resource.Dispose) is { } cleanup) throw cleanup;
            throw;
        }
    }

    internal static void Attach<T>(ref T? retained, Func<T> create, Action<T?> attach) where T : class, IDisposable
    {
        T resource = retained ??= create();
        try { attach(resource); }
        catch (Exception error)
        {
            retained = null;
            // Detach first so the host releases its event handlers and buffers.
            // A failed detach must not prevent disposal of the owned manager.
            if (Cleanup(error, () => attach(null), resource.Dispose) is { } cleanup) throw cleanup;
            throw;
        }
    }

    private static InvalidOperationException? Cleanup(Exception original, params Action[] actions)
    {
        List<Exception>? failures = null;
        foreach (var action in actions)
        {
            try { action(); }
            catch (Exception error) { (failures ??= [original]).Add(error); }
        }
        if (failures == null) return null;
        string message = original.Message;
        if (message.Length > 512) message = message[..512] + "…";
        // AggregateException.Message appends all inner messages. Keep those full
        // exceptions as evidence without expanding the GUI's visible diagnostic.
        return new InvalidOperationException("3D preview initialization failed: " + message + " Cleanup also failed.", new AggregateException(failures));
    }
}
