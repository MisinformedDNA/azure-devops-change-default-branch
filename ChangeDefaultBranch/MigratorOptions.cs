using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Policy.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace ChangeDefaultBranch
{
    public record MigratorOptions(string CurrentBranch, string NewBranch, GitHttpClient GitClient, BuildHttpClient BuildClient, PolicyHttpClient PolicyClient);
}
