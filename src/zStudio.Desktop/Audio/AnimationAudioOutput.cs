using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Recoil.Zbd.Desktop.Audio;

internal interface IAnimationAudioOutput : IDisposable
{
    event Action<string>? Failed;
    void Start(ISampleProvider source);
}

/// <summary>Created, started and disposed exclusively on background workers.</summary>
internal sealed class AnimationAudioOutput : IAnimationAudioOutput
{
    private WasapiPlayer? player;
    public event Action<string>? Failed;
    public void Start(ISampleProvider source)
    {
        player = new WasapiPlayerBuilder().WithSharedMode().WithLatency(50).Build();
        player.PlaybackStopped += (_, args) => { if (args.Exception != null) Failed?.Invoke(args.Exception.Message); };
        player.Init(new SampleToWaveProvider(source));
        player.Play();
    }
    public void Dispose() => player?.Dispose();
}
