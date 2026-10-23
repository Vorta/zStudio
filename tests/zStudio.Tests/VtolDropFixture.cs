using System.Text;
using zStudio.Core.Animation;
using zStudio.Core.Worlds;

namespace zStudio.Tests;

/// <summary>One ten-second authored flight and two whole release windows; no name-based VTOL recognition.</summary>
internal static class VtolDropFixture
{
    internal const string Root = "data/m1/zrdr/anim.zad";
    internal static string Definitions(string root = "ground") => $$"""
        # Retain this comment and the original spacing when a window moves.
        ANIMATION_DEFINITIONS (
          ANIMATION_LIST (
            ANIMATION_DEFINITION (
              NAME ( {{root}} ) ANIMATION_NAME ( delivery ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )
              SEQUENCE_DEFINITION (
                NAME ( route )
                CALL_SEQUENCE ( NAME ( drop_schedule ) )
                OBJECT_MOTION_SI_SCRIPT ( NAME ( {{root}} ) SCRIPT_FRAME_RATE ( 10 ) SCRIPT_FILENAME ( flight.zan ) )
                LOOP ( LOOP_COUNT ( -1 ) )
              )
              SEQUENCE_DEFINITION (
                NAME ( drop_schedule ) ACTIVATION ( ON_CALL )
                STOP_ANIMATION ( NAME ( callback ) )
                CALL_ANIMATION ( NAME ( callback ) START_TIME ( SEQUENCE_OFFSET 1 ) )
                STOP_ANIMATION ( NAME ( callback ) START_TIME ( EVENT_OFFSET 2 ) )
                CALL_ANIMATION ( NAME ( callback ) START_TIME ( SEQUENCE_OFFSET 5 ) )
                STOP_ANIMATION ( NAME ( callback ) START_TIME ( EVENT_OFFSET 2 ) )
              )
            )
            ANIMATION_DEFINITION (
              NAME ( {{root}} ) ANIMATION_NAME ( callback ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )
              SEQUENCE_DEFINITION (
                NAME ( releases )
                IF ( PLAYER_RANGE ( 300 ) ) CALLBACK ( VALUE ( 911 ) ) ENDIF ()
                LOOP ( LOOP_COUNT ( -1 ) START_TIME ( EVENT_OFFSET 0.5 ) )
              )
            )
          )
        )
        """;

    internal const string MotionScript = """
        OBJECT ground
        FRAME 0 POSITION 0 0 0
        FRAME 25 POSITION 10 0 10
        FRAME 50 POSITION 0 0 0
        FRAME 75 POSITION 10 0 -10
        FRAME 100 POSITION 0 0 0
        """;

    internal static MemoryFiles Files() => new(new(StringComparer.OrdinalIgnoreCase)
    {
        [Root] = Encoding.Latin1.GetBytes(Definitions()),
        ["data/m1/zrdr/flight.zan"] = Encoding.ASCII.GetBytes(MotionScript)
    });

    internal static AnimationCompiler.Result Compile(MemoryFiles files, CancellationToken token = default) =>
        AnimationCompiler.Compile(files, Root, ["ground"], token);

    internal sealed class MemoryFiles(Dictionary<string, byte[]> content) : IProjectFiles
    {
        internal Dictionary<string, byte[]> Content { get; } = content;
        public bool Exists(string relative) => Content.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        { token.ThrowIfCancellationRequested(); byte[] bytes = Content[relative]; limits.Validate(bytes); return bytes; }
    }
}
