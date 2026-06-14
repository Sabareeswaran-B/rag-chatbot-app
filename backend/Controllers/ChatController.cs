using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

/// <summary>Chat — send a question and receive an AI answer grounded in uploaded documents.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    public ChatController(IChatService chatService) => _chatService = chatService;

    /// <summary>
    /// Ask a question. The API embeds the query, retrieves the top-5 relevant document chunks via
    /// vector search, and sends them as context to GPT-4o-mini to generate a grounded answer.
    /// Works for both authenticated users and anonymous users (no JWT required).
    /// </summary>
    /// <param name="request">The user's question and an optional anonymous identifier.</param>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new ChatResponse { Success = false, Error = "Query cannot be empty." });

        var anonymousId = request.AnonymousId
            ?? Request.Headers["X-Anonymous-Id"].FirstOrDefault()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        try
        {
            var response = await _chatService.GetAnswerAsync(request.Query);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ChatResponse { Success = false, Error = ex.Message });
        }
    }
}
