using NetCord;
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
        await feedbackService.RecordRatingAsync(
            interaction.Message.Id,
            interaction.User.Id,
            interaction.Channel!.Id,
            helpful: true);

        await RespondAsync(InteractionCallback.ModifyMessage(m => m.Components = []));
    }

    [ComponentInteraction("feedback_not_helpful")]
    public Task NotHelpful()
    {
        // Open a modal asking for an optional reason. The modal submit handler does the
        // actual save — clicking the button alone is not enough signal without context.
        // botMessageId is encoded in the custom ID so submit handler can map to the row.
        var botMessageId = Context.Interaction.Message.Id;
        return RespondAsync(InteractionCallback.Modal(new ModalProperties(
            $"feedback_reason:{botMessageId}",
            "Help us improve",
            [
                new LabelProperties(
                    "What was wrong with the answer?",
                    new TextInputProperties("reason", TextInputStyle.Paragraph)
                    {
                        Placeholder = "Optional — leave blank if you'd rather not say.",
                        Required = false,
                        MaxLength = 500
                    })
            ])));
    }

    [ComponentInteraction("feedback_why")]
    public async Task Why()
    {
        var thought = await feedbackService.GetThoughtSummaryAsync(Context.Interaction.Message.Id);

        var description = string.IsNullOrWhiteSpace(thought)
            ? "No reasoning was captured for this response. (Older responses pre-date feedback persistence.)"
            : thought;

        await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties
        {
            Embeds =
            [
                new EmbedProperties
                {
                    Title = "Why this answer?",
                    Description = description.Length > 4096 ? description[..4093] + "..." : description,
                    Color = new Color(120, 144, 156)
                }
            ],
            Flags = MessageFlags.Ephemeral
        }));
    }
}
