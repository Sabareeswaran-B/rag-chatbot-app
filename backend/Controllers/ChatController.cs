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
public class ChatController(IChatService chatService) : ControllerBase
{
    private string GetUserId() =>
        User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? Request.Headers["X-Anonymous-Id"].FirstOrDefault()
        ?? HttpContext.Connection.RemoteIpAddress?.ToString()
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

        try
        {
            var userId = GetUserId();
            var response = await chatService.GetAnswerAsync(request.Query, request.SessionId, userId);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ChatResponse { Success = false, Error = ex.Message });
        }
    }
}
