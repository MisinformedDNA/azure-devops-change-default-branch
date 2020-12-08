using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChangeDefaultBranch.ConsoleApp
{
    public class Startup : IHostedService
    {
        private readonly Migrator _migrator;
        private readonly ILogger<Startup> _logger;

        public Startup(Migrator migrator, ILogger<Startup> logger)
        {
            _migrator = migrator;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            bool migrateAnother = true;

            while (migrateAnother)
            {
                try
                {
                    await _migrator.MigrateAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Migration failed");
                }

                Console.WriteLine("Would you like to perform another migration? [Y/N] ");
                var response = Console.ReadKey().KeyChar;
                if (char.ToUpperInvariant(response) != 'Y')
                    migrateAnother = false;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
