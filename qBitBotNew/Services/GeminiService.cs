using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using qBitBotNew.Config;
using qBitBotNew.Models;

namespace qBitBotNew.Services;

public sealed class GeminiService(HttpClient httpClient, IOptions<GeminiConfig> geminiConfig, IOptions<BotConfig> botConfig, ILogger<GeminiService> logger)
{
    private const string SystemPrompt = """
        You are a qBitTorrent support assistant in a Discord server. ONLY answer qBitTorrent Desktop client, WebUI, and API questions.

        ## Intent classification
        - "on_topic": qBitTorrent usage, config, troubleshooting, features, performance. Seeding, peers,
          trackers, port forwarding, client settings are ALL on-topic regardless of content transferred.
        - "piracy": EXPLICITLY requesting help with illegal activity (finding copyrighted content,
          evading detection, cracking). Normal client operations are NOT piracy.
        - "off_topic": Unrelated to qBitTorrent.

        ## Response rules
        - Low confidence: provide resource links, don't guess. Medium: answer + include resources.
        - Be brief and direct. Under 2000 chars. No preamble, no restating the problem.
        - Use Discord markdown (bold, lists, \n for line breaks in JSON). Don't single-line everything.
        - If unsolvable client-side, say so plainly. Short honest > long unhelpful.
        - If you identify a root cause that makes other steps irrelevant, lead with it directly.
          Don't soften it, bury it as a footnote, or pad around it with generic advice.
        - If your answer would meaningfully change depending on details you don't have, use the
          follow_up_questions field to ask. Be specific to what the user is describing — don't
          run through a generic checklist. You may still give a partial or conditional answer alongside.
        - On follow-up turns, don't re-ask what the user already told you.

        ## Conversation context
        - You may receive multi-turn conversation history. Your previous responses appear as "model" turns.
          Avoid repeating the same advice verbatim, but if the user is still confused or asks again,
          rephrase or elaborate — they may need a different explanation, not a refusal.
        - Background context messages are formatted as "[HH:mm] Name: text". Focus on answering
          [Current question] or [Primary question]; the rest is background.

        ## Resources and URLs
        - Only include URLs you are certain exist. Use base URLs to known pages
          (e.g., "https://github.com/qbittorrent/qBittorrent/wiki/Frequently-Asked-Questions").
        - Do NOT fabricate URL fragments or anchors. If you are not certain a #fragment exists on the
          page, link to the page without the fragment.
        """;

    private static readonly object ResponseSchema = new
    {
        type = "object",
        properties = new
        {
            intent = new
            {
                type = "string",
                @enum = new[] { "on_topic", "piracy", "off_topic" },
                description = "on_topic: question about qBitTorrent client usage/config/troubleshooting (including seeding, peers, trackers, port forwarding). piracy: explicitly asking for help with illegal activity (finding copyrighted content, avoiding detection). off_topic: nothing to do with qBitTorrent."
            },
            confidence = new
            {
                type = "string",
                @enum = new[] { "high", "medium", "low" }
            },
            response = new
            {
                type = "string",
                description = "Helpful answer to the qBitTorrent question. Empty if intent is not on_topic. Use Discord markdown formatting with \\n for line breaks, numbered lists, and bold text."
            },
            resources = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Relevant documentation links or resources when confidence is low/medium."
            },
            reasoning = new
            {
                type = "string",
                description = "Brief internal reasoning for your classification and response. Explain: what you understood the question to be, why you chose the intent, whether any attached images influenced your answer, and why you chose this confidence level."
            },
            follow_up_questions = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Specific questions to ask the user when critical environment details are missing for troubleshooting. Empty when the answer is complete or the question is simple."
            }
        },
        required = new[] { "intent", "confidence", "response", "resources", "reasoning", "follow_up_questions" }
    };

    public async Task<GeminiResponse?> AskAsync(List<GeminiMessage> conversation, List<AttachmentInfo>? attachments = null)
    {
        var cfg = geminiConfig.Value;
        var maxAttachmentBytes = botConfig.Value.MaxAttachmentBytes;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.Model}:generateContent?key={cfg.ApiKey}";

        logger.LogDebug("Gemini request — {TurnCount} turn(s), last: {LastMessage}",
            conversation.Count, conversation[^1].Content);

        // Build content turns from conversation
        var contents = new List<object>();
        var imagesAttached = 0;
        var imagesSkipped = 0;

        for (var i = 0; i < conversation.Count; i++)
        {
            var turn = conversation[i];
            var parts = new List<object> { new { text = turn.Content } };

            // Attach images to the last user message
            if (i == conversation.Count - 1 && turn.Role is "user" && attachments is { Count: > 0 })
            {
                logger.LogDebug("Processing {Count} attachment(s)", attachments.Count);

                foreach (var attachment in attachments)
                {
                    if (!IsImageContentType(attachment.ContentType))
                    {
                        imagesSkipped++;
                        logger.LogDebug("Skipping non-image attachment: {ContentType} — {Url}", attachment.ContentType, attachment.Url);
                        continue;
                    }

                    try
                    {
                        var imageBytes = await httpClient.GetByteArrayAsync(attachment.Url);

                        if (imageBytes.Length > maxAttachmentBytes)
                        {
                            imagesSkipped++;
                            logger.LogWarning("Skipping oversized attachment: {Size} bytes exceeds {Max} byte limit — {Url}",
                                imageBytes.Length, maxAttachmentBytes, attachment.Url);
                            continue;
                        }

                        var base64 = Convert.ToBase64String(imageBytes);
                        parts.Add(new
                        {
                            inline_data = new
                            {
                                mime_type = attachment.ContentType,
                                data = base64
                            }
                        });
                        imagesAttached++;
                        logger.LogDebug("Attached image: {ContentType}, {Size} bytes — {Url}", attachment.ContentType, imageBytes.Length, attachment.Url);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to download attachment: {Url}", attachment.Url);
                    }
                }
            }

            contents.Add(new { role = turn.Role, parts });
        }

        logger.LogDebug("Sending to Gemini — {Turns} turn(s), {Images} image(s) attached, {Skipped} skipped",
            contents.Count, imagesAttached, imagesSkipped);

        var requestPayload = new
        {
            system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents,
            tools = new[] { new { google_search = new { } } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseJsonSchema = ResponseSchema
            }
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync(url, requestPayload);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            var text = json
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Gemini returned empty text response");
                return null;
            }

            logger.LogDebug("Gemini raw response: {RawJson}", text);

            GeminiResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<GeminiResponse>(text);
            }
            catch (JsonException jsonEx)
            {
                logger.LogError(jsonEx, "Failed to deserialize Gemini response: {RawJson}", text);
                return null;
            }

            if (result is not null)
            {
                logger.LogDebug("Gemini result — Intent: {Intent}, Confidence: {Confidence}, Reasoning: {Reasoning}",
                    result.Intent, result.Confidence, result.Reasoning);
                logger.LogDebug("Gemini result — Response length: {Length}, Resources: {Resources}",
                    result.Response.Length, result.Resources.Count);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gemini API call failed");
            return null;
        }
    }

    private static bool IsImageContentType(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
