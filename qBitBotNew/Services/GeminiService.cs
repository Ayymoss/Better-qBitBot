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
        qBitTorrent support assistant. ONLY answer qBitTorrent Desktop client, WebUI, and API questions.

        Set "intent":
        - "on_topic": qBitTorrent usage, config, troubleshooting, features, performance. Seeding, peers,
          trackers, port forwarding, client settings are ALL on-topic regardless of content transferred.
        - "piracy": EXPLICITLY requesting help with illegal activity (finding copyrighted content,
          evading detection, cracking). Normal client operations are NOT piracy.
        - "off_topic": Unrelated to qBitTorrent.

        Response rules:
        - Low confidence: provide resource links, don't guess. Medium: answer + include resources.
        - Be brief and direct. 2-4 short paragraphs or a list, under 1500 chars. No preamble, no
          restating the problem. Answer, then stop.
        - Use Discord markdown (bold, lists, \n for line breaks in JSON). Don't single-line everything.
        - Lead with the root cause when obvious. Don't pad with generic steps that won't help.
          If unsolvable client-side (no seeders, dead tracker), say so plainly. Short honest > long unhelpful.

        Context: messages formatted as "[HH:mm] Name: text". Answer [Current question] or [Primary question];
        the rest is background.
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
