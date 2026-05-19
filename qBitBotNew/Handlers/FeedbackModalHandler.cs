using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed class FeedbackModalHandler(FeedbackService feedbackService)
    : ComponentInteractionModule<ModalInteractionContext>
{
    // CustomId pattern matches the modal opened by FeedbackButtonHandler.NotHelpful.
    // The botMessageId is parsed from the route so we can update the right Feedback row.
    [ComponentInteraction("feedback_reason")]
    public async Task Submit(ulong botMessageId)
    {
        var interaction = Context.Interaction;

        // Components[0] is the Label wrapping our TextInput "reason".
        var reason = Context.Components
            .OfType<Label>()
            .Select(l => l.Component)
            .OfType<TextInput>()
            .FirstOrDefault(t => t.CustomId == "reason")?
            .Value;

        await feedbackService.RecordRatingAsync(
            botMessageId,
            interaction.User.Id,
            interaction.Channel!.Id,
            helpful: false,
            reason: string.IsNullOrWhiteSpace(reason) ? null : reason);

        await RespondAsync(InteractionCallback.ModifyMessage(m => m.Components = []));
    }
}
