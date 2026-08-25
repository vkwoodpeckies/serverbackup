using BackupManager.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Host.CreateDefaultBuilder(args).UseWindowsService(options => options.ServiceName = "Backup Manager Service").ConfigureServices(services => { services.AddSingleton<ArchiveService>(); services.AddHostedService<SchedulerWorker>(); }).Build().Run();
sealed class SchedulerWorker(ILogger<SchedulerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Backup Manager Service started.");
        while (!stoppingToken.IsCancellationRequested) { /* Jobs are loaded from SQLite by the repository implementation. */ await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
    }
}
