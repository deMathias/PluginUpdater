using ExileCore2.Shared.Attributes;
using ExileCore2;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WheresMyPluginsAt
{
    [Submenu(RenderMethod = nameof(Render))]
    public class PluginRenderer : IDisposable
    {
        private readonly WheresMyPluginsAtSettings Settings;
        private readonly GitUpdater updater;
        private readonly ConsoleLog consoleLog = new ConsoleLog();
        private bool _isUpdating;
        private readonly Dictionary<string, bool> _updatingPlugins = [];
        private readonly Dictionary<string, bool> _revertingPlugins = [];
        private int _currentProgress;
        private int _totalProgress;
        private bool _isUpdatingAll;
        private readonly StringBuilder _logBuffer = new();
        private readonly object _logLock = new();
        private string _repoUrl = string.Empty;
        private bool _isCloning;
        private readonly List<(string Name, string CloneUrl)> _availablePlugins = new();
        private readonly Dictionary<string, bool> _downloadingPlugins = [];
        private bool _isLoadingRepos;
        private bool _hasLoadedRepos;
        private string _loadError = string.Empty;

        public PluginRenderer(WheresMyPluginsAtSettings settings)
        {
            Settings = settings;
            updater = new GitUpdater(Path.Combine(Environment.CurrentDirectory, "Plugins", "Source"));
            updater.ProgressChanged += (current, total) =>
            {
                _currentProgress = current;
                _totalProgress = total;
            };

            var manuallyDownloadedPlugins = updater.GetManualDownloadedPlugins();
            foreach (var plugin in manuallyDownloadedPlugins)
            {
                consoleLog.LogWarning($"{Path.GetFileName(plugin)} was downloaded manually so cannot be updated via this plugin");
            }
        }

        private async Task UpdateGitInfoAsync()
        {
            if (_isUpdating) return;

            try
            {
                _isUpdating = true;
                await updater.UpdateGitInfoAsync();
            }
            catch (Exception e)
            {
                var errorMsg = $"Error updating git info: {e.Message}";
                DebugWindow.LogError(errorMsg);
                consoleLog.LogError($"{errorMsg}");
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private async Task UpdatePluginAsync(string pluginName)
        {
            if (_updatingPlugins.TryGetValue(pluginName, out bool isUpdating) && isUpdating)
                return;

            try
            {
                _updatingPlugins[pluginName] = true;
                consoleLog.LogInfo($"Starting update for {pluginName}...");
                await updater.UpdatePluginAsync(pluginName);
                var plugin = updater.GetPluginInfo().FirstOrDefault(p => p.Name == pluginName);
                if (plugin != null && !string.IsNullOrEmpty(plugin.LastMessage))
                {
                    var lines = plugin.LastMessage.Split('\n');
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            consoleLog.LogInfo($"{line.Trim()}");
                        }
                    }
                }
                consoleLog.LogSuccess($"Successfully updated {pluginName}");
            }
            catch (Exception e)
            {
                var errorMsg = $"Error updating plugin {pluginName}: {e.Message}";
                DebugWindow.LogError(errorMsg);
                consoleLog.LogError($"{errorMsg}");
            }
            finally
            {
                _updatingPlugins[pluginName] = false;
            }
        }

        private async Task UpdateAllPluginsAsync()
        {
            if (_isUpdatingAll) return;

            try
            {
                _isUpdatingAll = true;
                var plugins = updater.GetPluginInfo();
                var outdatedPlugins = plugins.Where(p => p.CurrentCommit != p.LatestCommit).ToList();

                consoleLog.LogInfo($"Starting update for {outdatedPlugins.Count} plugins...");

                foreach (var plugin in outdatedPlugins)
                {
                    await UpdatePluginAsync(plugin.Name);
                }
            }
            catch (Exception e)
            {
                DebugWindow.LogError($"Error updating all plugins: {e.Message}");
            }
            finally
            {
                _isUpdatingAll = false;
                Settings.GameController.Memory.Dispose();
            }
        }

        public void Render()
        {
            if (!Settings.Enable.Value)
                return;

            if (ImGui.BeginTabBar("PluginManagerTabs"))
            {
                if (ImGui.BeginTabItem("Manage"))
                {
                    ImGui.Spacing();
                    RenderUpdateButtons();
                    RenderPluginsTable();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Add"))
                {
                    ImGui.Spacing();
                    RenderAddPluginSection();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Browse"))
                {
                    ImGui.Spacing();
                    RenderPluginBrowser();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            consoleLog.RenderConsoleLog();
        }

        private void RenderUpdateButtons()
        {
            if (_isUpdating)
            {
                ImGui.BeginDisabled();
                string progressText = _totalProgress > 0
                    ? $"Checking For Updates... ({_currentProgress}/{_totalProgress})"
                    : "Checking For Updates...";
                ImGui.Button(progressText);

                if (_totalProgress > 0)
                {
                    float progress = (float)_currentProgress / _totalProgress;
                    ImGui.ProgressBar(progress, new System.Numerics.Vector2(-1, 2));
                }

                ImGui.EndDisabled();
            }
            else
            {
                if (ImGui.Button("Check For Updates"))
                {
                    _currentProgress = 0;
                    _totalProgress = 0;
                    _ = UpdateGitInfoAsync();
                }

                var plugins = updater.GetPluginInfo();
                bool hasUpdates = plugins.Any(p => p.CurrentCommit != p.LatestCommit);

                if (hasUpdates)
                {
                    ImGui.SameLine();

                    if (_isUpdatingAll)
                    {
                        ImGui.BeginDisabled();
                        ImGui.Button("Updating All...");
                        ImGui.EndDisabled();
                    }
                    else
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0, 0.5f, 0, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0, 0.7f, 0, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0, 0.3f, 0, 1.0f));

                        if (ImGui.Button("Update All"))
                        {
                            _ = UpdateAllPluginsAsync();
                        }

                        ImGui.PopStyleColor(3);
                    }
                }
            }
        }

        private void RenderPluginsTable()
        {
            var tableFlags = ImGuiTableFlags.Borders |
                            ImGuiTableFlags.Resizable |
                            ImGuiTableFlags.SizingFixedFit |
                            ImGuiTableFlags.ScrollY |
                            ImGuiTableFlags.ScrollX |
                            ImGuiTableFlags.RowBg;

            const float ROW_HEIGHT = 25;
            const float HEADER_HEIGHT = 30;
            var plugins = updater.GetPluginInfo();
            float tableHeight = (plugins.Count * ROW_HEIGHT) + HEADER_HEIGHT;

            if (!ImGui.BeginTable("##table1", 3, tableFlags, new System.Numerics.Vector2(-1, tableHeight)))
                return;

            SetupTableColumns();
            ImGui.TableHeadersRow();
            ImGui.TableNextColumn();

            RenderTableRows();

            ImGui.EndTable();
        }

        private static void SetupTableColumns()
        {
            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Commit", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed);
        }

        private void RenderTableRows()
        {
            var plugins = updater.GetPluginInfo();

            foreach (var pluginInfo in plugins)
            {
                try
                {
                    RenderPluginName(pluginInfo);
                    RenderCommitInfo(pluginInfo);
                    RenderUpdateButton(pluginInfo);
                }
                catch (Exception e)
                {
                    DebugWindow.LogError($"Error rendering plugin {pluginInfo.Name}: {e.Message}");
                }
            }
        }

        private static void RenderPluginName(PluginInfo pluginInfo)
        {
            ImGui.Text(pluginInfo.Name);
            ImGui.TableNextColumn();
        }

        private static void RenderCommitInfo(PluginInfo pluginInfo)
        {
            if (string.IsNullOrEmpty(pluginInfo.LatestCommit))
            {
                ImGui.Text("No commit information");
            }
            else if (pluginInfo.CurrentCommit == pluginInfo.LatestCommit)
            {
                ImGui.Text($"Currently on latest commit ({pluginInfo.CurrentCommit})");
            }
            else
            {
                ImGui.Text($"{pluginInfo.CurrentCommit} -> {pluginInfo.LatestCommit} ({pluginInfo.BehindAhead})");
            }
            ImGui.TableNextColumn();
        }

        private async Task RevertPluginAsync(string pluginName)
        {
            if (_revertingPlugins.TryGetValue(pluginName, out bool isReverting) && isReverting)
                return;

            try
            {
                _revertingPlugins[pluginName] = true;
                consoleLog.LogInfo($"Starting revert for {pluginName}...");
                await updater.RevertPluginAsync(pluginName);
                var plugin = updater.GetPluginInfo().FirstOrDefault(p => p.Name == pluginName);
                if (plugin != null && !string.IsNullOrEmpty(plugin.LastMessage))
                {
                    var lines = plugin.LastMessage.Split('\n');
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            consoleLog.LogInfo($"{line.Trim()}");
                        }
                    }
                }
                consoleLog.LogSuccess($"Successfully reverted {pluginName}");
            }
            catch (Exception e)
            {
                var errorMsg = $"Error reverting plugin {pluginName}: {e.Message}";
                DebugWindow.LogError(errorMsg);
                consoleLog.LogError($"{errorMsg}");
            }
            finally
            {
                _revertingPlugins[pluginName] = false;
            }
        }

        private void RenderUpdateButton(PluginInfo pluginInfo)
        {
            if (string.IsNullOrEmpty(pluginInfo.CurrentCommit))
            {
                ImGui.TableNextColumn();
                return;
            }

            bool isUpdating = _updatingPlugins.TryGetValue(pluginInfo.Name, out bool updating) && updating;
            bool isReverting = _revertingPlugins.TryGetValue(pluginInfo.Name, out bool reverting) && reverting;

            if (pluginInfo.CurrentCommit != pluginInfo.LatestCommit)
            {
                if (isUpdating)
                {
                    ImGui.BeginDisabled();
                    ImGui.Button($"Updating...##{pluginInfo.Name}");
                    ImGui.EndDisabled();
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0, 0.5f, 0, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0, 0.7f, 0, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0, 0.3f, 0, 1.0f));

                    if (ImGui.Button($"Update##{pluginInfo.Name}"))
                    {
                        _ = UpdatePluginAsync(pluginInfo.Name);
                    }

                    ImGui.PopStyleColor(3);
                }
            }
            else
            {
                if (isReverting)
                {
                    ImGui.BeginDisabled();
                    ImGui.Button($"Reverting...##{pluginInfo.Name}");
                    ImGui.EndDisabled();
                }
                else if (ImGui.Button($"Revert##{pluginInfo.Name}"))
                {
                    _ = RevertPluginAsync(pluginInfo.Name);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Revert to the previous commit (HEAD~1)");
                }
            }
            ImGui.TableNextColumn();
        }

        private void RenderAddPluginSection()
        {
            ImGui.Text("Enter Repository URL:");

            var width = ImGui.GetContentRegionAvail().X - 100;
            ImGui.SetNextItemWidth(width);
            ImGui.InputText("##repoinput", ref _repoUrl, 1024);

            ImGui.SameLine();

            if (_isCloning)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Cloning...");
                ImGui.EndDisabled();
            }
            else if (ImGui.Button("Clone"))
            {
                if (!string.IsNullOrWhiteSpace(_repoUrl))
                {
                    _ = CloneRepositoryAsync(_repoUrl);
                }
                else
                {
                    consoleLog.LogWarning("Please enter a repository URL");
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Clone the repository into the plugins folder");
            }

            ImGui.Spacing();
            ImGui.TextWrapped("Enter the URL of a Git repository to clone. " +
                             "The repository will be cloned into the Plugins\\Source folder.");
        }

        private async Task CloneRepositoryAsync(string repoUrl)
        {
            if (_isCloning) return;

            try
            {
                _isCloning = true;
                consoleLog.LogInfo($"Cloning repository: {repoUrl}");

                await updater.CloneRepositoryAsync(repoUrl);

                consoleLog.LogSuccess($"Successfully cloned repository");
                _repoUrl = string.Empty;
            }
            catch (Exception ex)
            {
                consoleLog.LogError($"Error cloning repository: {ex.Message}");
            }
            finally
            {
                _isCloning = false;
            }
        }

        private async Task LoadRepositoriesAsync()
        {
            if (_isLoadingRepos || _hasLoadedRepos) return;

            try
            {
                _isLoadingRepos = true;
                _loadError = string.Empty;

                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("WheresMyPluginsAt", "1.0"));

                var response = await client.GetStringAsync("https://api.github.com/orgs/exCore2/repos");

                using var document = System.Text.Json.JsonDocument.Parse(response);
                var repos = document.RootElement.EnumerateArray();

                _availablePlugins.Clear();
                foreach (var repo in repos)
                {
                    var name = repo.GetProperty("name").GetString();
                    var cloneUrl = repo.GetProperty("clone_url").GetString();

                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(cloneUrl))
                    {
                        _availablePlugins.Add((name, cloneUrl));
                    }
                }

                _hasLoadedRepos = true;
            }
            catch (Exception ex)
            {
                _loadError = $"Error loading repositories: {ex.Message}";
                consoleLog.LogError(_loadError);
            }
            finally
            {
                _isLoadingRepos = false;
            }
        }

        private void RenderPluginBrowser()
        {
            if (!_hasLoadedRepos && !_isLoadingRepos)
            {
                _ = LoadRepositoriesAsync();
            }

            ImGui.TextWrapped("Browse and download available plugins from the ExileCore2 repo");
            ImGui.Spacing();

            if (_isLoadingRepos)
            {
                ImGui.TextWrapped("Loading available plugins...");
                return;
            }

            if (!string.IsNullOrEmpty(_loadError))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1.0f, 0.2f, 0.2f, 1.0f));
                ImGui.TextWrapped(_loadError);
                ImGui.PopStyleColor();

                if (ImGui.Button("Retry"))
                {
                    _hasLoadedRepos = false;
                    _loadError = string.Empty;
                    _ = LoadRepositoriesAsync();
                }
                return;
            }

            var tableFlags = ImGuiTableFlags.Borders |
                            ImGuiTableFlags.Resizable |
                            ImGuiTableFlags.SizingFixedFit |
                            ImGuiTableFlags.ScrollY |
                            ImGuiTableFlags.RowBg;

            const float ROW_HEIGHT = 25;
            const float HEADER_HEIGHT = 30;
            var installedPlugins = updater.GetPluginInfo();
            var availablePlugins = _availablePlugins.Where(p =>
                !installedPlugins.Any(ip => ip.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            if (availablePlugins.Count == 0)
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.2f, 1.0f, 0.2f, 1.0f),
                    _availablePlugins.Count > 0
                        ? "All available plugins are already installed!"
                        : "No plugins found in the ExileCore2 repo.");
                return;
            }

            float tableHeight = (availablePlugins.Count * ROW_HEIGHT) + HEADER_HEIGHT;

            if (!ImGui.BeginTable("##browsertable", 2, tableFlags, new System.Numerics.Vector2(-1, tableHeight)))
                return;

            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            foreach (var plugin in availablePlugins)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                ImGui.Text(plugin.Name);
                ImGui.TableNextColumn();

                bool isDownloading = _downloadingPlugins.TryGetValue(plugin.Name, out bool downloading) && downloading;

                if (isDownloading)
                {
                    ImGui.BeginDisabled();
                    ImGui.Button($"Downloading...##{plugin.Name}");
                    ImGui.EndDisabled();
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0, 0.5f, 0, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0, 0.7f, 0, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0, 0.3f, 0, 1.0f));

                    if (ImGui.Button($"Download##{plugin.Name}"))
                    {
                        _ = CloneRepositoryAsync(plugin.CloneUrl);
                    }

                    ImGui.PopStyleColor(3);
                }
            }

            ImGui.EndTable();
        }

        public void Dispose()
        {
            updater?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}