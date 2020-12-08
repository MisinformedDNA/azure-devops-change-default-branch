using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChangeDefaultBranch
{
    public class Migrator
    {
        private readonly MigratorOptions _options;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<Migrator> _logger;

        public Migrator(MigratorOptions options, ILoggerFactory loggerFactory)
        {
            _options = options;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<Migrator>();
        }

        public async Task MigrateAsync()
        {
            var repositories = await GetRepositoriesAsync();

            Console.WriteLine("You have access to the following repositories:");

            for (int i = 0; i < repositories.Count; i++)
            {
                Console.WriteLine($"\t[{i + 1}] {repositories[i].Name}");
            }

            Console.Write("Which one would you like to migrate? (enter number) ");
            var response = Console.ReadLine();
            if (!int.TryParse(response, out var number))
                return;

            if (number < 1 || number > repositories.Count)
                return;

            var selectedIndex = number - 1;

            GitRepository repository = repositories[selectedIndex];
            Console.WriteLine($"You have chosen to migrate repository '{repository.Name}'");
            Console.Write("Is that correct? [Y/N] ");
            var keyResponse = Console.ReadKey().KeyChar;
            Console.WriteLine();
            if (char.ToUpper(keyResponse) != 'Y')
                return;

            var repoMigrator = new RepositoryMigrator(repository, _options, _loggerFactory);
            await repoMigrator.MigrateAsync();
        }

        private async Task<List<GitRepository>> GetRepositoriesAsync() => await _options.GitClient.GetRepositoriesAsync();
    }
}
