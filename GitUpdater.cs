using ExileCore2;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private IEnumerable<string> GetPluginsWithGit()
        {
            return Directory.GetDirectories(_pluginFolder)
                .Where(dir => Directory.Exists(Path.Combine(dir, ".git")));
        }

        public IEnumerable<string> GetManualDownloadedPlugins()
        {
            return Directory.GetDirectories(_pluginFolder)
                .Where(dir => !Directory.Exists(Path.Combine(dir, ".git")));
        }

        public GitUpdater(string pluginFolder)
        {
            _pluginFolder = pluginFolder;

            var folders = GetPluginsWithGit();
            foreach (var folder in folders)
            {
                var pluginInfo = new PluginInfo
                {
                    Name = Path.GetFileName(folder)
                };
                _pluginInfo.Add(pluginInfo);
            }
        }

        private static async Task<string> ExecuteGitCommandAsync(string command, string workingDirectory, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = command,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var processExitTask = Task.Run(() =>
                {
                    process.WaitForExit();
                }, cancellationToken);

                await Task.WhenAny(processExitTask, Task.Delay(-1, cancellationToken));

                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch (InvalidOperationException) { }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                string output = await outputTask;
                return output.Trim();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException) { }
                }
                throw;
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
                    var pluginInfo = new PluginInfo
                    {
                        Name = Path.GetFileName(folder)
                    };

                    string currentCommit = await ExecuteGitCommandAsync("log -1 --format=\"%h\"", folder, cancellationToken);
                    pluginInfo.CurrentCommit = currentCommit;

                    await ExecuteGitCommandAsync("fetch", folder, cancellationToken);

                    string currentBranch = await ExecuteGitCommandAsync("branch --show-current", folder, cancellationToken);
                    string trackingBranch = await ExecuteGitCommandAsync($"rev-parse --abbrev-ref {currentBranch}@{{u}}", folder, cancellationToken);
                    string latestCommit = await ExecuteGitCommandAsync($"log -1 --format=\"%h\" {trackingBranch}", folder, cancellationToken);

                    pluginInfo.LatestCommit = latestCommit;

                    if (currentCommit != latestCommit)
                    {
                        string behindAhead = await ExecuteGitCommandAsync($"rev-list --left-right --count {currentBranch}...{trackingBranch}", folder, cancellationToken);
                        string[] counts = behindAhead.Split('\t');
                        if (counts.Length == 2)
                        {
                            pluginInfo.BehindAhead = $"{counts[0]} behind, {counts[1]} ahead";
                        }
                    }

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
            if (folder == null)
            {
                return;
            }

            var plugin = _pluginInfo.FirstOrDefault(p => p.Name == pluginName);
            if (plugin == null)
            {
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource();

                string currentBranch = await ExecuteGitCommandAsync("branch --show-current", folder, cts.Token);
                plugin.LastMessage = await ExecuteGitCommandAsync("reset --hard HEAD~1", folder, cts.Token);
                plugin.CurrentCommit = await ExecuteGitCommandAsync("log -1 --format=\"%h\"", folder, cts.Token);

                string trackingBranch = await ExecuteGitCommandAsync($"rev-parse --abbrev-ref {currentBranch}@{{u}}", folder, cts.Token);
                string behindAhead = await ExecuteGitCommandAsync($"rev-list --left-right --count {currentBranch}...{trackingBranch}", folder, cts.Token);
                string[] counts = behindAhead.Split('\t');
                if (counts.Length == 2)
                {
                    plugin.BehindAhead = $"{counts[0]} behind, {counts[1]} ahead";
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
        }

        public async Task UpdatePluginAsync(string pluginName)
        {
            var folder = GetPluginsWithGit().FirstOrDefault(f => f.Contains(pluginName));
            if (folder == null)
            {
                return;
            }

            var plugin = _pluginInfo.FirstOrDefault(p => p.Name == pluginName);
            if (plugin == null)
            {
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource();
                plugin.LastMessage = await ExecuteGitCommandAsync("pull", folder, cts.Token);

                plugin.CurrentCommit = await ExecuteGitCommandAsync("log -1 --format=\"%h\"", folder, cts.Token);
                string currentBranch = await ExecuteGitCommandAsync("branch --show-current", folder, cts.Token);
                string trackingBranch = await ExecuteGitCommandAsync($"rev-parse --abbrev-ref {currentBranch}@{{u}}", folder, cts.Token);
                plugin.LatestCommit = await ExecuteGitCommandAsync($"log -1 --format=\"%h\" {trackingBranch}", folder, cts.Token);

                if (plugin.CurrentCommit != plugin.LatestCommit)
                {
                    string behindAhead = await ExecuteGitCommandAsync($"rev-list --left-right --count {currentBranch}...{trackingBranch}", folder, cts.Token);
                    string[] counts = behindAhead.Split('\t');
                    if (counts.Length == 2)
                    {
                        plugin.BehindAhead = $"{counts[0]} behind, {counts[1]} ahead";
                    }
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
        }

        public async Task CloneRepositoryAsync(string repoUrl)
        {
            string repoName = Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/'));

            if (string.IsNullOrEmpty(repoName))
            {
                repoName = repoUrl.Split('/').Last().Replace(".git", "");
            }

            string targetPath = Path.Combine(_pluginFolder, repoName);

            if (Directory.Exists(targetPath))
            {
                throw new InvalidOperationException($"A plugin with the name {repoName} already exists");
            }

            try
            {
                using var cts = new CancellationTokenSource();
                string output = await ExecuteGitCommandAsync($"clone {repoUrl}", _pluginFolder, cts.Token);

                var pluginInfo = new PluginInfo
                {
                    Name = repoName
                };

                string currentCommit = await ExecuteGitCommandAsync("log -1 --format=\"%h\"", targetPath, cts.Token);
                pluginInfo.CurrentCommit = currentCommit;

                string currentBranch = await ExecuteGitCommandAsync("branch --show-current", targetPath, cts.Token);
                string trackingBranch = await ExecuteGitCommandAsync($"rev-parse --abbrev-ref {currentBranch}@{{u}}", targetPath, cts.Token);
                pluginInfo.LatestCommit = await ExecuteGitCommandAsync($"log -1 --format=\"%h\" {trackingBranch}", targetPath, cts.Token);

                lock (_pluginInfo)
                {
                    _pluginInfo.Add(pluginInfo);
                }

                return;
            }
            catch (Exception)
            {
                if (Directory.Exists(targetPath))
                {
                    try
                    {
                        Directory.Delete(targetPath, true);
                    }
                    catch { }
                }
                throw;
            }
        }

        public List<PluginInfo> GetPluginInfo()
        {
            lock (_pluginInfo)
            {
                return new List<PluginInfo>(_pluginInfo);
            }
        }

        public void Dispose()
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}