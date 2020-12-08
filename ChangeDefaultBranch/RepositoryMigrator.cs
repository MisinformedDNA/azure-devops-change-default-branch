using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Policy.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChangeDefaultBranch
{
    public class RepositoryMigrator
    {
        private const string RefPrefix = "refs/heads/";
        private const string MirrorFileName = "/mirror.yml";
        private static readonly string EmptyObjectId = new string('0', 40);

        private readonly GitRepository _repository;
        private readonly GitHttpClient _gitClient;
        private readonly BuildHttpClient _buildClient;
        private readonly PolicyHttpClient _policyClient;
        private readonly string _currentBranch;
        private readonly string _newBranch;
        private readonly ILogger<RepositoryMigrator> _logger;

        public RepositoryMigrator(GitRepository repository, MigratorOptions options, ILoggerFactory loggerFactory)
        {
            _repository = repository;
            _gitClient = options.GitClient;
            _buildClient = options.BuildClient;
            _policyClient = options.PolicyClient;
            _currentBranch = options.CurrentBranch;
            _newBranch = options.NewBranch;
            _logger = loggerFactory.CreateLogger<RepositoryMigrator>();
        }

        public async Task MigrateAsync()
        {
            _logger.LogDebug($"Migrating repository '{_repository.Name}'");

            if (_repository.DefaultBranch == null)
                return;

            string repositoryName = _repository.Name;

            // Step 1: Create new branch
            if (IsDefaultBranch(_currentBranch) && !await ContainsBranch(_newBranch))
            {
                _logger.LogDebug($"Creating new branch for '{repositoryName}'");
                await CreateNewBranchAsync();
            }

            // Step 2: Set default branch
            if (IsDefaultBranch(_currentBranch))
            {
                _logger.LogDebug($"Setting default branch for '{repositoryName}'");
                await SetDefaultBranchAsync(_newBranch);
            }

            // Step 3: Create a pipeline file in the new branch (The commit is purposefully made before policies are enabled.)
            if (await MirroringPipelineFileDoesNotExist())
            {
                _logger.LogDebug($"Creating pipeline file for '{repositoryName}'");
                await CreateMirroringPipelineFileAsync(_newBranch);
            }

            // Step 4: Create mirroring pipeline
            string mirroringPipelineName = GetMirroringPipelineName();
            if (!await PipelineExistsAsync(mirroringPipelineName))
            {
                await CreateMirroringPipelineAsync();
            }

            // Step 5: CopyBranchPolicies
            await CopyBranchPolicies(_currentBranch, _newBranch);

            // Step 6: Update existing pipelines with the new branch name
            await UpdatePipelinesAsync(_currentBranch, _newBranch);

            // Cleanup
            //await SetDefaultBranchAsync(_currentBranch);
            //await DeleteBranchAsync(_newBranch);
            //await DeletePipelineAsync(mirroringPipelineName);
        }

        #region Branches

        private async Task CreateNewBranchAsync()
        {
            var branch = await _gitClient.GetBranchAsync(_repository.Id, _currentBranch);

            var gitRefUpdate = new GitRefUpdate
            {
                Name = GetRefName(_newBranch),
                OldObjectId = EmptyObjectId,
                NewObjectId = branch.Commit.CommitId,
            };

            await _gitClient.UpdateRefsAsync(new[] { gitRefUpdate }, _repository.Id);
        }

        private async Task DeleteBranchAsync(string branchName)
        {
            var branch = await _gitClient.GetBranchAsync(_repository.Id, branchName);

            var gitRefUpdate = new GitRefUpdate
            {
                Name = GetRefName(branchName),
                OldObjectId = branch.Commit.CommitId,
                NewObjectId = EmptyObjectId,
            };

            await _gitClient.UpdateRefsAsync(new[] { gitRefUpdate }, _repository.Id);
        }

        private async Task SetDefaultBranchAsync(string branchName)
        {
            _repository.DefaultBranch = GetRefName(branchName);
            await _gitClient.UpdateRepositoryAsync(_repository, _repository.Id);
        }

        #endregion

        #region Pipelines

        private async Task CreateMirroringPipelineAsync()
        {
            try
            {
                var build = new BuildDefinition
                {
                    Name = GetMirroringPipelineName(),
                    Repository = new BuildRepository { Name = _repository.Name, Type = "TfsGit" },
                    Process = new YamlProcess { YamlFilename = MirrorFileName },
                    Type = DefinitionType.Build,
                };
                await _buildClient.CreateDefinitionAsync(build, _repository.ProjectReference.Name);
            }
            catch (VssUnauthorizedException e)
            {
                _logger.LogError(e, "Authorization failed for build client.");
                throw;
            }
        }

        private async Task DeletePipelineAsync(string pipelineName)
        {
            BuildDefinitionReference? definition = await GetPipelineAsync(pipelineName);
            if (definition is null)
                return;

            await _buildClient.DeleteDefinitionAsync(_repository.ProjectReference.Id, definition.Id);
        }

        private async Task<bool> PipelineExistsAsync(string pipelineName)
        {
            var definitions = await GetPipelineAsync(pipelineName);
            return definitions != null;
        }

        private async Task<BuildDefinitionReference?> GetPipelineAsync(string pipelineName)
        {
            var definitions = await _buildClient.GetDefinitionsAsync2(_repository.ProjectReference.Id, pipelineName);
            return definitions.SingleOrDefault();
        }

        private string GetMirroringPipelineName()
        {
            return $"{_repository.Name} mirroring pipeline";
        }

        #endregion

        #region Pipeline files

        private async Task CreateMirroringPipelineFileAsync(string branchName)
        {
            var branch = await _gitClient.GetBranchAsync(_repository.Id, branchName);

            var gitRef = new GitRefUpdate
            {
                Name = GetRefName(branchName),
                OldObjectId = branch.Commit.CommitId,
            };

            var mirrorFileContents = $@"
trigger:
  branches:
    include:
    - {_currentBranch}
    - {_newBranch}
 
pool: {{ vmImage: ubuntu-latest }}
steps:
- checkout: self
  persistCredentials: true
- script: |
    git checkout $(Build.SourceBranchName)
    git push origin HEAD:{_currentBranch} HEAD:{_newBranch}
  displayName: Mirror old and new default branches
";

            var commit = new GitCommit
            {
                Comment = "Add mirroring pipeline",
                Changes = new[]
                {
                    new GitChange
                    {
                        ChangeType = VersionControlChangeType.Add,
                        Item = new GitItem { Path = MirrorFileName },
                        NewContent = new ItemContent
                        {
                            Content = mirrorFileContents,
                            ContentType = ItemContentType.RawText
                        }
                    }
                }
            };

            var push = new GitPush
            {
                RefUpdates = new[] { gitRef },
                Commits = new[] { commit }
            };

            await _gitClient.CreatePushAsync(push, _repository.Id);
        }

        private async ValueTask<bool> MirroringPipelineFileDoesNotExist()
        {
            var items = await _gitClient.GetItemsAsync(_repository.Id, scopePath: "/", recursionLevel: VersionControlRecursionType.OneLevel);
            var mirrorFile = items.SingleOrDefault(i => i.Path == MirrorFileName);

            return mirrorFile is null;
        }

        #endregion

        #region Branch policies

        private async Task CopyBranchPolicies(string sourceBranch, string targetBranch)
        {
            var policyResponse = await _gitClient.GetPolicyConfigurationsAsync(_repository.ProjectReference.Id, _repository.Id, refName: GetRefName(sourceBranch));
            var policies = policyResponse.PolicyConfigurations;

            foreach (var policy in policies)
            {
                try
                {
                    await CloneBranchPolicy(policy, targetBranch);
                }
                catch (VssServiceException e) when (e.Message == "The update is rejected by policy.")
                {
                    var sb = new StringBuilder()
                        .AppendLine($"ERROR: '{nameof(CloneBranchPolicy)}' failed for repository {_repository.Name}.")
                        .Append("DATA: " + JsonConvert.SerializeObject(policy));
                    _logger.LogDebug(e, sb.ToString());
                }
            }
        }

        private async Task CloneBranchPolicy(PolicyConfiguration policy, string targetBranch)
        {
            dynamic settings = (dynamic)policy.Settings;

            foreach (var scope in settings.scope)
            {
                if (scope.refName != null)
                    scope.refName = GetRefName(targetBranch);
                else
                {
                    string scopeJson = JsonConvert.SerializeObject(settings.scope);
                    _logger.LogDebug($"Scope not modified: {scopeJson}");
                }
            }

            var newPolicy = new PolicyConfiguration()
            {
                IsBlocking = policy.IsBlocking,
                IsDeleted = policy.IsDeleted,
                IsEnabled = policy.IsEnabled,
                Type = policy.Type,
                Settings = (JObject)settings,
            };

            await _policyClient.CreatePolicyConfigurationAsync(newPolicy, _repository.ProjectReference.Name);
        }

        #endregion

        #region Update pipelines

        private async Task UpdatePipelinesAsync(string currentBranch, string newBranch)
        {
            var definitions = await _buildClient.GetFullDefinitionsAsync2(_repository.ProjectReference.Id, repositoryId: _repository.Id.ToString(), repositoryType: "TfsGit");
            foreach (var definition in definitions)
            {
                await UpdatePipelineAsync(definition, currentBranch, newBranch);
            }
        }

        private async Task UpdatePipelineAsync(BuildDefinition definition, string currentBranch, string newBranch)
        {
            _logger.LogDebug($"Updating build definition '{definition.Name}' in project {_repository.ProjectReference.Name}");

            var changesExist = false;

            if (definition.Repository.DefaultBranch == GetRefName(currentBranch))
            {
                definition.Repository.DefaultBranch = GetRefName(newBranch);
                changesExist = true;
            }

            foreach (var trigger in definition.Triggers)
            {
                switch (trigger)
                {
                    case ContinuousIntegrationTrigger t:
                        for (int i = 0; i < t.BranchFilters.Count; i++)
                        {
                            t.BranchFilters[i] = ChangeBranchFilter(t.BranchFilters[i]);
                        }
                        break;
                    case PullRequestTrigger t:
                        for (int i = 0; i < t.BranchFilters.Count; i++)
                        {
                            t.BranchFilters[i] = ChangeBranchFilter(t.BranchFilters[i]);
                        }
                        break;
                    default:
                        _logger.LogDebug($"Trigger not processed: {JsonConvert.SerializeObject(trigger)}");
                        break;
                }
            }

            if (changesExist)
            {
                await _buildClient.UpdateDefinitionAsync(definition, _repository.ProjectReference.Name);
            }

            string ChangeBranchFilter(string branchFilter)
            {
                if (branchFilter.Contains(GetRefName(_currentBranch)))
                {
                    changesExist = true;
                    return branchFilter.Replace(GetRefName(_currentBranch), GetRefName(_newBranch));
                }

                return branchFilter;
            }
        }

        #endregion

        private async ValueTask<bool> ContainsBranch(string branchName)
        {
            var branches = await _gitClient.GetBranchesAsync(_repository.Id);
            return branches.Any(b => b.Name == branchName);
        }

        private static string GetRefName(string branchName) => branchName.StartsWith(RefPrefix) ? branchName : $"{RefPrefix}{branchName}";

        private bool IsDefaultBranch(string branchName) => _repository.DefaultBranch == GetRefName(branchName);

        private object ReplaceBranchFilters(BuildTrigger trigger)
        {
            throw new NotImplementedException();
        }
    }
}
