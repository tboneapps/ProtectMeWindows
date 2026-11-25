using ProtectMe.Core;
using ProtectMe.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ProtectMeService";
});

builder.Services.AddSingleton<ProtectionStateManager>();
builder.Services.AddHostedService<ProcessBlockerWorker>();
builder.Services.AddHostedService<WhitelistProxyWorker>();
builder.Services.AddHostedService<PipeServerWorker>();

var host = builder.Build();
host.Run();
