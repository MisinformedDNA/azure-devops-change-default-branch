using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Policy.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Threading.Tasks;
using static System.Console;

namespace ChangeDefaultBranch.ConsoleApp
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var organization = Prompt("Enter organization name");
            var pat = Prompt("Enter personal access token", isSecret: true);
            var currentBranch = Prompt("Enter old default branch name", defaultValue: "master");
            var newBranch = Prompt("Enter new default branch name", defaultValue: "main");

            var collectionUri = $"https://dev.azure.com/{organization}";
            var creds = new VssBasicCredential(string.Empty, pat);
            pat = null;     // Overwrite secret
            var connection = new VssConnection(new Uri(collectionUri), creds);

            MigratorOptions migratorOptions;
            try
            {
                var gitClient = await connection.GetClientAsync<GitHttpClient>();
                var buildClient = await connection.GetClientAsync<BuildHttpClient>();
                var policyClient = await connection.GetClientAsync<PolicyHttpClient>();

                migratorOptions = new MigratorOptions(currentBranch, newBranch, gitClient, buildClient, policyClient);
            }
            catch (VssServiceException)
            {
                WriteLine("An error occurred.");
                Environment.Exit(-1);
                return;
            }

            CreateHostBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddTransient(s => migratorOptions);
                })
                .Build()
                .Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddTransient<Migrator>();
                    services.AddHostedService<Startup>();
                });


        private static string Prompt(string promptText, bool isSecret = false, string? defaultValue = null)
        {
            if (string.IsNullOrWhiteSpace(defaultValue))
                defaultValue = null;

            string? value;
            do
            {
                Write($"{promptText}: ");
                if (defaultValue is not null)
                    Write($"({defaultValue}) ");

                value = !isSecret ? ReadLine() : ReadSecret();

                if (string.IsNullOrWhiteSpace(value) && defaultValue is not null)
                {
                    return defaultValue;
                }

            } while (string.IsNullOrWhiteSpace(value));

            return value;
        }

        private static string ReadSecret()
        {
            string password = string.Empty;

            while (true)
            {
                var key = ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    WriteLine();
                    break;
                }
                password += key.KeyChar;
            }

            return password;
        }
    }
}
