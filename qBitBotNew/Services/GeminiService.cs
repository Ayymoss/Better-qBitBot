using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using qBitBotNew.Config;
using qBitBotNew.Models;

namespace qBitBotNew.Services;

public sealed class GeminiService(HttpClient httpClient, IOptions<GeminiConfig> config, ILogger<GeminiService> logger)
{
    private const string SystemPrompt = """
        You are a helpful qBitTorrent support assistant. You ONLY answer questions about the
        qBitTorrent desktop client — configuration, troubleshooting, features, and usage.

        Classification — set "intent" to one of:
        - "on_topic": The question is about qBitTorrent client usage, configuration, troubleshooting,
          performance tuning, or features. Questions about seeding, leeching, peer connections, tracker
          configuration, port forwarding, and client settings ARE on-topic — these are normal client
          operations regardless of what content is being transferred.
        - "piracy": The question is EXPLICITLY asking for help with illegal activity — e.g. where to
          find copyrighted content, how to avoid detection while pirating, cracking software, etc.
          Do NOT flag questions about normal torrent client usage as piracy just because torrents are
          mentioned. Seeding, peer counts, tracker configuration, and client performance are on-topic.
        - "off_topic": The question has nothing to do with qBitTorrent at all (e.g. general chat,
          questions about other software, unrelated topics).

        Rules:
        - When confidence is "low", provide helpful resource links instead of guessing.
        - When confidence is "medium", answer but also include resources.
        - Keep responses concise and actionable (under 1500 characters for Discord).
        - Format your response using Discord markdown. Where needed, use numbered lists, bullet points, bold,
          and short paragraphs. Use \n for line breaks within the JSON response string — do NOT
          put everything on a single line.
        - Be direct and honest. If the evidence (e.g. screenshots, stated facts) makes the root cause
          obvious, lead with that conclusion. Do NOT pad the response with generic troubleshooting steps
          that won't help. For example, if a torrent has 0 seeds, say clearly that no client configuration
          can fix this — the content simply has no active uploaders. Only suggest troubleshooting steps
          that could plausibly change the outcome given the specific situation.
        - Avoid the "infinite troubleshooting treadmill." If something is definitively unsolvable from
          the client side (e.g. no seeders exist, tracker is dead), say so plainly and briefly. It is
          better to give a short honest answer than a long unhelpful one.

        The user's question (possibly multi-message) follows:
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
            }
        },
        required = new[] { "intent", "confidence", "response", "resources", "reasoning" }
    };

    public async Task<GeminiResponse?> AskAsync(string question, List<AttachmentInfo>? attachments = null)
    {
        var cfg = config.Value;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.Model}:generateContent?key={cfg.ApiKey}";

        logger.LogDebug("Gemini request — Question: {Question}", question);

        // Add text part with system prompt + question
        var parts = new List<object>
        {
            new { text = $"{SystemPrompt}\n\n{question}" }
        };

        // Add image parts if any
        var imagesAttached = 0;
        var imagesSkipped = 0;

        if (attachments is { Count: > 0 })
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

        logger.LogDebug("Sending to Gemini — {TextParts} text part(s), {Images} image(s) attached, {Skipped} skipped",
            1, imagesAttached, imagesSkipped);

        var requestPayload = new
        {
            contents = new[] { new { parts } },
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

            var result = JsonSerializer.Deserialize<GeminiResponse>(text);

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
