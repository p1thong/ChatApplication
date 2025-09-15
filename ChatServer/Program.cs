using System.Net;
using ChatServer;

var builder = WebApplication.CreateBuilder(args);

// Giữ web server trên cổng 8080
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Any, 8080);
});

builder.Services.AddSingleton<ChatSocketServer>();
builder.Services.AddHostedService<ChatSocketService>();

var app = builder.Build();

app.MapGet("/", () => "Chat Server is running!");

app.Run();
