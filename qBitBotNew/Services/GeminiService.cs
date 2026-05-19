using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using qBitBotNew.Config;
using qBitBotNew.Models;

namespace qBitBotNew.Services;

public sealed partial class GeminiService(HttpClient httpClient, IOptions<GeminiConfig> geminiConfig, IOptions<BotConfig> botConfig, ILogger<GeminiService> logger)
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
                @enum = new[] { "low", "medium", "high" }
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

    public async Task<Result<GeminiResponse>> AskAsync(List<GeminiMessage> conversation, List<AttachmentInfo>? attachments = null, CancellationToken ct = default)
    {
        var cfg = geminiConfig.Value;
        var maxAttachmentBytes = botConfig.Value.MaxAttachmentBytes;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.Model}:generateContent?key={cfg.ApiKey}";

        LogRequest(conversation.Count, conversation[^1].Content);

        // Build content turns from conversation
        List<object> contents = [];
        var imagesAttached = 0;
        var imagesSkipped = 0;

        for (var i = 0; i < conversation.Count; i++)
        {
            var turn = conversation[i];
            List<object> parts = [new { text = turn.Content }];

            // Attach images to the last user message
            if (i == conversation.Count - 1 && turn.Role is "user" && attachments is { Count: > 0 })
            {
                LogProcessingAttachments(attachments.Count);

                foreach (var attachment in attachments)
                {
                    if (!IsImageContentType(attachment.ContentType))
                    {
                        imagesSkipped++;
                        LogSkippingNonImage(attachment.ContentType, attachment.Url);
                        continue;
                    }

                    try
                    {
                        var imageBytes = await httpClient.GetByteArrayAsync(attachment.Url, ct);

                        if (imageBytes.Length > maxAttachmentBytes)
                        {
                            imagesSkipped++;
                            LogSkippingOversized(imageBytes.Length, maxAttachmentBytes, attachment.Url);
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
                        LogAttachedImage(attachment.ContentType, imageBytes.Length, attachment.Url);
                    }
                    catch (Exception ex)
                    {
                        LogDownloadAttachmentFailed(ex, attachment.Url);
                    }
                }
            }

            contents.Add(new { role = turn.Role, parts });
        }

        LogSendingToGemini(contents.Count, imagesAttached, imagesSkipped);

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
            var response = await httpClient.PostAsJsonAsync(url, requestPayload, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            var text = json
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(text))
            {
                LogEmptyResponse();
                return Result<GeminiResponse>.Failure("Gemini returned an empty response.");
            }

            LogRawResponse(text);

            try
            {
                var result = JsonSerializer.Deserialize<GeminiResponse>(text);
                if (result is null)
                {
                    LogDeserializationNull();
                    return Result<GeminiResponse>.Failure("Failed to deserialize Gemini response.");
                }

                LogSuccess(result.Intent, result.Confidence, result.Reasoning, result.Response.Length, result.Resources.Count);
                return result;
            }
            catch (JsonException jsonEx)
            {
                LogDeserializationFailed(jsonEx, text);
                return Result<GeminiResponse>.Failure($"Failed to parse Gemini response JSON: {jsonEx.Message}");
            }
        }
        catch (Exception ex)
        {
            LogApiCallFailed(ex);
            return Result<GeminiResponse>.Failure($"Gemini API call failed: {ex.Message}");
        }
    }

    private static bool IsImageContentType(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gemini request — {TurnCount} turn(s), last: {LastMessage}")]
    private partial void LogRequest(int turnCount, string lastMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing {Count} attachment(s)")]
    private partial void LogProcessingAttachments(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping non-image attachment: {ContentType} — {Url}")]
    private partial void LogSkippingNonImage(string contentType, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping oversized attachment: {Size} bytes exceeds {Max} byte limit — {Url}")]
    private partial void LogSkippingOversized(long size, long max, string url);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Attached image: {ContentType}, {Size} bytes — {Url}")]
    private partial void LogAttachedImage(string contentType, int size, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download attachment: {Url}")]
    private partial void LogDownloadAttachmentFailed(Exception ex, string url);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending to Gemini — {Turns} turn(s), {Images} image(s) attached, {Skipped} skipped")]
    private partial void LogSendingToGemini(int turns, int images, int skipped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gemini returned empty text response")]
    private partial void LogEmptyResponse();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gemini raw response: {RawJson}")]
    private partial void LogRawResponse(string rawJson);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to deserialize Gemini response: {RawJson}")]
    private partial void LogDeserializationFailed(Exception ex, string rawJson);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gemini returned null after deserialization")]
    private partial void LogDeserializationNull();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gemini result — Intent: {Intent}, Confidence: {Confidence}, Reasoning: {Reasoning}, Length: {Length}, Resources: {Resources}")]
    private partial void LogSuccess(string intent, ConfidenceLevel confidence, string reasoning, int length, int resources);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gemini API call failed")]
    private partial void LogApiCallFailed(Exception ex);
}
