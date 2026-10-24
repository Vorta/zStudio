namespace zStudio.Core.Language;

public static partial class RecoilLanguage
{
    private static readonly IReadOnlyDictionary<int, string> CallbackIds = new Dictionary<int, string>
    {
        [0] = "Clear the active debug-script entry when it is this animation.",
        [1] = "Rebuild camera direction and bind the active save state.",
        [2] = "Unbind the single-player save state and hide the HUD and reticle.",
        [10] = "Synchronize the local player's pose from its root node and show HUD stacks.",
        [11] = "Suspend single-player control and update modal-loop sound state.",
        [14] = "Disable single-player controls/HUD and disable turret ticking.",
        [15] = "Restore single-player controls/HUD according to HUD settings and enable turret ticking.",
        [16] = "Reset the player's transient motion state.",
        [17] = "Capture the current object pose as the restart anchor.",
        [20] = "Mark this animation as the active debug-script entry.",
        [25] = "Clear the nanite panel, then enter the shared destruction path.",
        [26] = "Enter the shared player destruction path.",
        [27] = "Enter player destruction with the fixed value 10.",
        [99] = "Save and queue mission state.",
        [911] = "Try to spawn a random pickup through the global airdrop controller.",
        [912] = "Spawn pickup type 32 at carrier node vwbus.",
        [913] = "Spawn pickup type 36 at carrier node crbox.",
        [914] = "Spawn pickup type 30 at carrier node drop."
    };
    private static string CallbackHelp(int id) => $"**CALLBACK event ID {id}**\n\n" + (CallbackIds.TryGetValue(id, out var description) ? description : "No meaning is recorded for this ID in Player::AsyncCommandCallback.")
        + "\n\nThese meanings belong to the game's registered Player::AsyncCommandCallback handler. Another registered handler can interpret the ID differently; unknown IDs are not rejected by the language service. Without a handler, the event has no effect. zStudio preview records this event without executing gameplay code.";
    private static string ReadableShape(string shape) => string.Join(", ", shape.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => part == "..." ? "additional values" :
        (part.EndsWith('?') ? "optional " : "") + (part[0] switch { 's' => "text", 'i' => "stored integer", 'f' => "stored float", 'n' => "number", _ => "value" })));
    private static string FieldDocumentation(LanguageField field) => $"**{field.Name}**\n\n{field.Description}"
        + (field.Arguments != null ? "\n\n**Arguments:** " + field.Arguments + "." : "")
        + (field.Shape.Length > 0 ? "\n\n**Value types:** " + ReadableShape(field.Shape) + "." : "")
        + (field.Notes != null ? "\n\n" + field.Notes : "")
        + (field.Choices is { Length: > 0 } choices ? "\n\n**Suggested values:** " + string.Join(", ", choices.Select(c => "`" + c + "`")) + "." : "")
        + (field.Example != null ? "\n\n```gamez-script\n" + field.Example + "\n```" : "");
    private static int ArgumentIndex(LanguageDocument document, int offset)
    {
        int argument = 0;
        foreach (var token in document.Tokens)
        {
            if (token.Span.Start >= offset) break;
            if (token.Kind == "delimiter") { if (token.Text == "(") argument = 0; }
            else if (token.Span.End < offset || token.Span.End == offset && offset < document.Text.Length && char.IsWhiteSpace(document.Text[offset])) argument++;
        }
        return argument;
    }
}
