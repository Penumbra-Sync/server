using Microsoft.AspNetCore.SignalR;

// this is a very hacky way to attach this file server to the main mare hub signalr instance via redis
// signalr publishes the namespace and hubname into the redis backend so this needs to be equal to the original
// but I don't need to reimplement the hub completely as I only exclusively use it for internal connection calling
// from the queue service so I keep the namespace and name of the class the same so it can connect to the same channel
// if anyone finds a better way to do this let me know

#pragma warning disable IDE0130 // Namespace does not match folder structure
#pragma warning disable MA0048 // File name must match type name
namespace MareSynchronosServer.Hubs;
public class MareHub : Hub
{
    public override Task OnConnectedAsync()
    {
        throw new NotSupportedException();
    }

    public override Task OnDisconnectedAsync(Exception exception)
    {
        throw new NotSupportedException();
    }
}
#pragma warning restore IDE0130 // Namespace does not match folder structure
#pragma warning restore MA0048 // File name must match type name