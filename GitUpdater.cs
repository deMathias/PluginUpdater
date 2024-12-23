﻿using ExileCore2;
using LibGit2Sharp;
using System;
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
        private readonly List<PluginInfo> _pluginInfo = [];
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
                _pluginInfo.Add(new PluginInfo { Name = Path.GetFileName(folder) });
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

                        var trackingBranch = repo.Head.TrackedBranch;
                        if (trackingBranch != null)
                        {
                            pluginInfo.LatestCommit = trackingBranch.Tip.Sha[..7];

                            if (pluginInfo.CurrentCommit != pluginInfo.LatestCommit)
                            {
                                var ahead = repo.Head.TrackingDetails.AheadBy ?? 0;
                                var behind = repo.Head.TrackingDetails.BehindBy ?? 0;
                                pluginInfo.BehindAhead = $"{behind} behind, {ahead} ahead";
                            }
                        }
                    }, cancellationToken);

                    lock (_pluginInfo)
                    {
                        var existingPlugin = _pluginInfo.FirstOrDefault(p => p.Name == pluginInfo.Name);
                        if (existingPlugin != null)
                        {
                            _pluginInfo[_pluginInfo.IndexOf(existingPlugin)] = pluginInfo;
                        }
                        else
                        {
                            _pluginInfo.Add(pluginInfo);
                        }
                    }
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

            var plugin = _pluginInfo.FirstOrDefault(p => p.Name == pluginName);
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

            var plugin = _pluginInfo.FirstOrDefault(p => p.Name == pluginName);
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
                        : $"Updated to {mergeResult.Commit.Id}";

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

        public async Task CloneRepositoryAsync(string repoUrl)
        {
            string repoName = Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/'));

            if (string.IsNullOrEmpty(repoName))
                repoName = repoUrl.Split('/').Last().Replace(".git", "");

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

                    Repository.Clone(repoUrl, targetPath, cloneOptions);

                    using var repo = new Repository(targetPath);
                    var pluginInfo = new PluginInfo
                    {
                        Name = repoName,
                        CurrentCommit = repo.Head.Tip.Sha[..7]
                    };

                    var trackingBranch = repo.Head.TrackedBranch;
                    if (trackingBranch != null)
                        pluginInfo.LatestCommit = trackingBranch.Tip.Sha[..7];

                    lock (_pluginInfo)
                        _pluginInfo.Add(pluginInfo);
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

                lock (_pluginInfo)
                {
                    var plugin = _pluginInfo.FirstOrDefault(p =>
                        p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
                    if (plugin != null)
                        _pluginInfo.Remove(plugin);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete plugin: {ex.Message}", ex);
            }
        }

        public List<PluginInfo> GetPluginInfo()
        {
            lock (_pluginInfo)
                return new List<PluginInfo>(_pluginInfo);
        }

        public void Dispose()
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}