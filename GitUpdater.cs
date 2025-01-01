using ExileCore2;
using LibGit2Sharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WheresMyPluginsAt
{
    public class PluginInfo
    {
        public string Name { get; set; }
        public string CurrentCommit { get; set; } = "";
        public string LatestCommit { get; set; } = "";
        public string BehindAhead { get; set; } = "";
        public string LastMessage { get; set; } = "";
    }

    public class GitUpdater : IDisposable
    {
        private readonly string _pluginFolder;
        private readonly ConcurrentDictionary<string, PluginInfo> _pluginInfo = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _updateCts;
        private Task _updateTask;

        public event Action<int, int> ProgressChanged;
        private void ReportProgress(int current, int total) => ProgressChanged?.Invoke(current, total);

        private IEnumerable<string> GetPluginsWithGit() =>
            Directory.GetDirectories(_pluginFolder).Where(dir => Directory.Exists(Path.Combine(dir, ".git")));

        public IEnumerable<string> GetManualDownloadedPlugins() =>
            Directory.GetDirectories(_pluginFolder).Where(dir => !Directory.Exists(Path.Combine(dir, ".git")));

        public GitUpdater(string pluginFolder)
        {
            _pluginFolder = pluginFolder;
            var folders = GetPluginsWithGit();
            foreach (var folder in folders)
            {
                var pluginInfo = new PluginInfo { Name = Path.GetFileName(folder) };
                _pluginInfo[pluginInfo.Name] = pluginInfo;
            }
        }

        public async Task UpdateGitInfoAsync()
        {
            if (_updateTask != null && !_updateTask.IsCompleted)
            {
                var result = MessageBox.Show(
                    "Update in progress, do you want to restart?",
                    "Update in progress",
                    MessageBoxButtons.YesNo);

                if (result == DialogResult.No)
                    return;

                _updateCts?.Cancel();

                try
                {
                    await Task.WhenAny(_updateTask, Task.Delay(5000));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    DebugWindow.LogError($"Error during task cancellation: {ex.Message}");
                }
            }

            _updateCts = new CancellationTokenSource();

            try
            {
                _updateTask = UpdateGitInfoInternalAsync(_updateCts.Token);
                await _updateTask;
            }
            catch (OperationCanceledException)
            {
                DebugWindow.LogMsg("Git update operation was cancelled");
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"Error during git update: {ex.Message}");
                throw;
            }
        }

        private async Task UpdateGitInfoInternalAsync(CancellationToken cancellationToken)
        {
            var folders = GetPluginsWithGit().ToList();
            int totalFolders = folders.Count;
            int currentFolder = 0;

            ReportProgress(0, totalFolders);

            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var pluginInfo = new PluginInfo { Name = Path.GetFileName(folder) };

                    await Task.Run(() =>
                    {
                        using var repo = new Repository(folder);
                        pluginInfo.CurrentCommit = repo.Head.Tip.Sha[..7];

                        var remote = repo.Network.Remotes["origin"];
                        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);

                        var fetchOptions = new FetchOptions
                        {
                            CredentialsProvider = (_url, _user, _cred) => new DefaultCredentials()
                        };

                        Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);

                        string branchName = repo.Head.FriendlyName;
                        var trackingBranch = repo.Head.TrackedBranch;

                        if (trackingBranch == null)
                        {
                            var remoteBranch = repo.Branches[$"origin/{branchName}"];
                            if (remoteBranch != null)
                            {
                                repo.Branches.Update(repo.Head,
                                    b => b.TrackedBranch = remoteBranch.CanonicalName);
                                trackingBranch = remoteBranch;
                            }
                        }

                        if (trackingBranch != null)
                        {
                            pluginInfo.LatestCommit = trackingBranch.Tip.Sha[..7];

                            var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(repo.Head.Tip, trackingBranch.Tip);
                            if (divergence != null)
                            {
                                var ahead = divergence.AheadBy ?? 0;
                                var behind = divergence.BehindBy ?? 0;
                                pluginInfo.BehindAhead = $"{behind} behind, {ahead} ahead";
                            }
                        }
                    }, cancellationToken);

                    _pluginInfo[pluginInfo.Name] = pluginInfo;
                }
                catch (Exception e)
                {
                    DebugWindow.LogError($"Error processing {folder}: {e.Message}");
                }
                finally
                {
                    currentFolder++;
                    ReportProgress(currentFolder, totalFolders);
                }
            }
        }

        public async Task RevertPluginAsync(string pluginName)
        {
            var folder = GetPluginsWithGit().FirstOrDefault(f => f.Contains(pluginName));
            if (folder == null) return;

            var plugin = _pluginInfo.GetValueOrDefault(pluginName);
            if (plugin == null) return;

            await Task.Run(() =>
            {
                try
                {
                    using var repo = new Repository(folder);
                    var currentBranch = repo.Head;
                    var parent = repo.Head.Tip.Parents.FirstOrDefault() ?? throw new Exception("Cannot revert: no parent commit found");
                    repo.Reset(ResetMode.Hard, parent);
                    plugin.LastMessage = $"Reset to parent commit {parent.Sha[..7]}";
                    plugin.CurrentCommit = parent.Sha[..7];

                    var trackingBranch = currentBranch.TrackedBranch;
                    if (trackingBranch != null)
                    {
                        var ahead = repo.Head.TrackingDetails.AheadBy ?? 0;
                        var behind = repo.Head.TrackingDetails.BehindBy ?? 0;
                        plugin.BehindAhead = $"{behind} behind, {ahead} ahead";
                    }

                    DebugWindow.LogMsg($"{folder} reverted to {plugin.CurrentCommit}");
                    DebugWindow.LogMsg(plugin.LastMessage);
                }
                catch (Exception ex)
                {
                    string errorMsg = $"Error reverting plugin {pluginName}: {ex.Message}";
                    DebugWindow.LogError(errorMsg);
                    throw;
                }
            });
        }

        public async Task UpdatePluginAsync(string pluginName)
        {
            var folder = GetPluginsWithGit().FirstOrDefault(f => f.Contains(pluginName));
            if (folder == null) return;

            var plugin = _pluginInfo.GetValueOrDefault(pluginName);
            if (plugin == null) return;

            await Task.Run(() =>
            {
                try
                {
                    using var repo = new Repository(folder);

                    var remote = repo.Network.Remotes["origin"];
                    var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
                    Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
                    {
                        CredentialsProvider = (_url, _user, _cred) => new DefaultCredentials()
                    }, null);

                    var trackingBranch = repo.Head.TrackedBranch ?? throw new Exception("No tracking branch found");
                    var mergeResult = Commands.Pull(
                        repo,
                        new Signature("Plugin Updater", "updater@local", DateTimeOffset.Now),
                        new PullOptions());

                    plugin.LastMessage = mergeResult.Status == MergeStatus.UpToDate
                        ? "Already up to date"
                        : $"Updated to {mergeResult.Commit.Id.Sha[..7]}";

                    plugin.CurrentCommit = repo.Head.Tip.Sha[..7];
                    plugin.LatestCommit = trackingBranch.Tip.Sha[..7];

                    if (plugin.CurrentCommit != plugin.LatestCommit)
                    {
                        var ahead = repo.Head.TrackingDetails.AheadBy ?? 0;
                        var behind = repo.Head.TrackingDetails.BehindBy ?? 0;
                        plugin.BehindAhead = $"{behind} behind, {ahead} ahead";
                    }
                    else
                    {
                        plugin.BehindAhead = "";
                    }

                    DebugWindow.LogMsg($"{folder} updated to {plugin.CurrentCommit}");
                    DebugWindow.LogMsg(plugin.LastMessage);
                }
                catch (Exception ex)
                {
                    DebugWindow.LogError($"Error updating plugin {pluginName}: {ex.Message}");
                    throw;
                }
            });
        }

        private static string ExtractRepoNameAndBranch(string repoUrl, out string branch)
        {
            branch = null;

            int treeIndex = repoUrl.IndexOf("/tree/");
            if (treeIndex != -1)
            {
                branch = repoUrl[(treeIndex + 6)..];
                repoUrl = repoUrl[..treeIndex];
            }

            string repoName = Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/'));
            if (string.IsNullOrEmpty(repoName))
                repoName = repoUrl.Split('/').Last().Replace(".git", "");

            return repoName;
        }

        public async Task CloneRepositoryAsync(string repoUrl)
        {
            string repoName = ExtractRepoNameAndBranch(repoUrl, out string branch);
            string targetPath = Path.Combine(_pluginFolder, repoName);

            if (Directory.Exists(targetPath))
                throw new InvalidOperationException($"A plugin with the name {repoName} already exists");

            await Task.Run(() =>
            {
                try
                {
                    var fetchOptions = new FetchOptions
                    {
                        CredentialsProvider = (_url, _user, _cred) => new DefaultCredentials()
                    };

                    var cloneOptions = new CloneOptions
                    {
                        IsBare = false,
                        Checkout = true
                    };
                    cloneOptions.FetchOptions.CredentialsProvider = fetchOptions.CredentialsProvider;

                    string cleanUrl = repoUrl;
                    int treeIndex = cleanUrl.IndexOf("/tree/");
                    if (treeIndex != -1)
                    {
                        cleanUrl = cleanUrl[..treeIndex];
                        if (!cleanUrl.EndsWith(".git"))
                            cleanUrl += ".git";
                    }

                    Repository.Clone(cleanUrl, targetPath, cloneOptions);

                    if (!string.IsNullOrEmpty(branch))
                    {
                        using var branchRepo = new Repository(targetPath);
                        var targetBranch = branchRepo.Branches[$"origin/{branch}"] ?? throw new Exception($"Branch '{branch}' not found in repository");
                        var localBranch = branchRepo.CreateBranch(branch, targetBranch.Tip);
                        branchRepo.Branches.Update(localBranch,
                            b => b.TrackedBranch = targetBranch.CanonicalName);

                        Commands.Checkout(branchRepo, localBranch);
                    }

                    using var finalRepo = new Repository(targetPath);
                    var pluginInfo = new PluginInfo
                    {
                        Name = repoName,
                        CurrentCommit = finalRepo.Head.Tip.Sha[..7]
                    };

                    var trackingBranch = finalRepo.Head.TrackedBranch;
                    if (trackingBranch != null)
                        pluginInfo.LatestCommit = trackingBranch.Tip.Sha[..7];

                    _pluginInfo[pluginInfo.Name] = pluginInfo;
                }
                catch (Exception)
                {
                    if (Directory.Exists(targetPath))
                    {
                        try { Directory.Delete(targetPath, true); }
                        catch { }
                    }
                    throw;
                }
            });
        }

        public void DeletePlugin(string pluginName)
        {
            var pluginPath = Path.Combine(_pluginFolder, pluginName);

            if (!Directory.Exists(pluginPath))
                throw new DirectoryNotFoundException($"Plugin directory not found: {pluginPath}");

            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();

                using (var repo = new Repository(pluginPath)) { }

                foreach (var file in Directory.GetFiles(pluginPath, "*.*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                foreach (var dir in Directory.GetDirectories(pluginPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(dir, FileAttributes.Normal);

                Directory.Delete(pluginPath, true);

                _pluginInfo.TryRemove(pluginName, out _);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete plugin: {ex.Message}", ex);
            }
        }

        public List<PluginInfo> GetPluginInfo()
        {
            return _pluginInfo.Values.ToList();
        }

        public void Dispose()
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}