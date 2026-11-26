using System.Text;
using System.Text.Json;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>Cat psychologist chat: each message goes to Gemini through the platform gateway and uses credits.</summary>
[ApiController]
[Route("api/psychologist")]
public class PsychologistController : ControllerBase
{
    private const string GeminiPath = "/v1beta/models/gemini-3.5-flash-lite:generateContent";
    private const string UnavailableText = "The cat psychologist is unavailable right now. Please try again later.";
    private const int MaxMessageLength = 1000;
    private const int MaxHistoryTurns = 12;

    private const string SystemPrompt =
        "You are Dr. Whiskers, a kind and playful cat psychologist. Give calm, warm, practical advice about cat behavior, " +
        "with a light touch of humor. Keep each reply under 120 words. Ask one gentle follow-up question when it helps. " +
        "For anything that could be a health problem, suggest seeing a real vet.";

    public record ChatTurn(string Role, string Text);

    public record ChatRequest(string Message, List<ChatTurn>? History);

    /// <summary>POST /api/psychologist/message - Send the chat so far and get the next reply.</summary>
    [HttpPost("message")]
    public async Task<IActionResult> Message([FromBody] ChatRequest request)
    {
        var message = (request.Message ?? "").Trim();
        if (message.Length == 0)
            return BadRequest(new { error = "Write a message first." });
        if (message.Length > MaxMessageLength)
            return BadRequest(new { error = $"Please keep your message under {MaxMessageLength} characters." });
        if (!GoogleGateway.IsConfigured)
            return Unavailable();

        // Gemini expects the conversation to open with the user, so leading replies from the cat are skipped.
        var history = (request.History ?? new List<ChatTurn>())
            .Where(t => t.Role is "user" or "model" && !string.IsNullOrWhiteSpace(t.Text))
            .TakeLast(MaxHistoryTurns)
            .SkipWhile(t => t.Role == "model");

        var contents = new List<object>();
        foreach (var turn in history)
        {
            contents.Add(new { role = turn.Role, parts = new[] { new { text = Shorten(turn.Text) } } });
        }
        contents.Add(new { role = "user", parts = new[] { new { text = message } } });

        var body = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents,
            generationConfig = new { maxOutputTokens = 400, temperature = 0.8 },
        });

        using var response = await GoogleGateway.PostJsonAsync("gemini", GeminiPath, body);
        var responseText = await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode is 402 or 429)
            return PlatformRefusal(responseText);
        if (!response.IsSuccessStatusCode)
            return Unavailable();

        return Ok(new { reply = ReadReply(responseText) });
    }

    private static string Shorten(string text) => text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;

    private IActionResult Unavailable() => StatusCode(503, new { code = "unavailable", error = UnavailableText });

    // The platform's own refusals carry a code. A 402 or 429 without one comes from Google, so it shows as unavailable.
    private IActionResult PlatformRefusal(string responseText)
    {
        var code = ReadCode(responseText);
        return code switch
        {
            "out_of_credits" => StatusCode(402, new { code, error = "The cat psychologist is out of credits for now." }),
            "daily_credit_limit" or "daily_call_limit" or "run_call_limit" => StatusCode(429, new { code = "daily_limit", error = "The cat psychologist has reached today's limit. Please try again later today." }),
            _ => Unavailable(),
        };
    }

    private static string? ReadCode(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        return doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
    }

    private static string ReadReply(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        var reply = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                    reply.Append(text.GetString());
            }
        }
        return reply.Length > 0 ? reply.ToString().Trim() : "Hmm, let me think about that one. Could you tell me a little more?";
    }
}
