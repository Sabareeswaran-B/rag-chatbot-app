using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

/// <summary>Chat history — list, retrieve, and delete conversation sessions per user.</summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
[Produces("application/json")]
public class ChatHistoryController(IChatHistoryRepository repo) : ControllerBase
{
    private string GetUserId() =>
        User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? Request.Headers["X-Anonymous-Id"].FirstOrDefault()
        ?? "anonymous";

    /// <summary>Get the 30 most recent chat sessions for the current user (messages excluded).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ChatSessionSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ChatSessionSummary>>> GetHistory()
    {
        var sessions = await repo.GetRecentAsync(GetUserId(), 30);
        return Ok(sessions.Select(s => new ChatSessionSummary
        {
            Id = s.Id!,
            Name = s.Name,
            UpdatedAt = s.UpdatedAt,
            MessageCount = s.Messages.Count / 2
        }).ToList());
    }

    /// <summary>Get a specific session including all messages.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ChatSession), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatSession>> GetSession(string id)
    {
        var session = await repo.GetAsync(id, GetUserId());
        if (session == null) return NotFound();
        return Ok(session);
    }

    /// <summary>Delete a specific chat session.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSession(string id)
    {
        await repo.DeleteAsync(id, GetUserId());
        return NoContent();
    }

    /// <summary>Clear all chat history for the current user.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearAll()
    {
        await repo.DeleteAllAsync(GetUserId());
        return NoContent();
    }
}
