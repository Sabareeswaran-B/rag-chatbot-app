using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

/// <summary>Chat — send a question and receive an AI answer grounded in uploaded documents.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ChatController(IChatService chatService, ILogger<ChatController> logger) : ControllerBase
{
    private string GetUserId() =>
        User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? Request.Headers["X-Anonymous-Id"].FirstOrDefault()
        ?? "anonymous";

    /// <summary>
    /// Ask a question. Embeds query, retrieves top-5 chunks via vector search, generates a grounded
    /// answer with GPT-4o-mini, saves to session history, and returns sessionId for continuity.
    /// Works for authenticated users and anonymous users.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new ChatResponse { Success = false, Error = "Query cannot be empty." });

        var userId = GetUserId();
        logger.LogInformation("Chat request — userId={UserId} sessionId={SessionId} query={Query}",
            userId, request.SessionId ?? "new", request.Query.Length > 80 ? request.Query[..80] + "…" : request.Query);

        try
        {
            var response = await chatService.GetAnswerAsync(request.Query, request.SessionId, userId);

            if (response.Error != null)
                logger.LogWarning("Chat returned error — userId={UserId} error={Error}", userId, response.Error);
            else
                logger.LogInformation("Chat complete — userId={UserId} sessionId={SessionId} sources={SourceCount}",
                    userId, response.SessionId, response.Sources?.Count ?? 0);

            return Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat unhandled exception — userId={UserId} query={Query}", userId, request.Query);
            return StatusCode(500, new ChatResponse { Success = false, Error = ex.Message });
        }
    }
}
