using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagChatbot.API.Models;
using RagChatbot.API.Services;

namespace RagChatbot.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    public ChatController(IChatService chatService) => _chatService = chatService;

    [HttpPost]
    [AllowAnonymous]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new ChatResponse { Success = false, Error = "Query cannot be empty." });

        // Anonymous user identification
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
