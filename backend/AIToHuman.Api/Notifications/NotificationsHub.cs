using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace AIToHuman.Api.Notifications;

public sealed class NotificationsHub(IHostEnvironment environment) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userId, out var parsedUserId))
        {
            var queryUserId = Context.GetHttpContext()?.Request.Query["userId"].ToString();
            if (!environment.IsDevelopment() || !Guid.TryParse(queryUserId, out parsedUserId)) { Context.Abort(); return; }
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{parsedUserId:N}");
        await base.OnConnectedAsync();
    }
}
