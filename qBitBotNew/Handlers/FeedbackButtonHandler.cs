using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed class FeedbackButtonHandler(FeedbackService feedbackService)
    : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("feedback_helpful")]
    public async Task Helpful()
    {
        var interaction = Context.Interaction;
        feedbackService.LogFeedback(interaction.Message.Id, interaction.User.Id, interaction.Channel!.Id, helpful: true);

        // Update the original message to remove buttons
        await RespondAsync(InteractionCallback.ModifyMessage(m => m.Components = []));
    }

    [ComponentInteraction("feedback_not_helpful")]
    public async Task NotHelpful()
    {
        var interaction = Context.Interaction;
        feedbackService.LogFeedback(interaction.Message.Id, interaction.User.Id, interaction.Channel!.Id, helpful: false);

        await RespondAsync(InteractionCallback.ModifyMessage(m => m.Components = []));
    }
}
