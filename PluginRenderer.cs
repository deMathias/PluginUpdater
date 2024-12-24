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
    public class PluginRepositoryData
    {
        public List<PluginDescription> PluginDescriptions { get; set; }
    }

    public class PluginDescription
    {
        public string Name { get; set; }
        public string OriginalAuthor { get; set; }
        public List<Fork> Forks { get; set; }
        public string Description { get; set; }
        public string EndorsedAuthor { get; set; }
    }

    public class Fork
    {
        public string Author { get; set; }
        public string Location { get; set; }
        public string Name { get; set; }
        public LatestCommit LatestCommit { get; set; }
        public List<Release> Releases { get; set; }
    }

    public class LatestCommit
    {
        public string Message { get; set; }
        public string Hash { get; set; }
        public string Author { get; set; }
        public string Date { get; set; }
    }

    public class Release
    {
        // the output.json has no data in releases so not sure structure xd
    }

    [Submenu(RenderMethod = nameof(Render))]
    public class PluginRenderer : IDisposable
    {
        public  readonly ConsoleLog consoleLog = new();
        private readonly WheresMyPluginsAtSettings Settings;
        private readonly GitUpdater updater;
        
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
        private readonly List<PluginDescription> _availablePlugins = [];
        private readonly Dictionary<string, bool> _downloadingPlugins = [];
        private bool _isLoadingRepos;
        private bool _hasLoadedRepos;
        private string _loadError = string.Empty;
        private DateTime _lastPeriodicCheckAttempt = DateTime.MinValue;

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

        public void Startup()
        {
            if (Settings.CheckUpdatesOnStartup && !Settings.HasCheckedUpdates)
            {
                Settings.HasCheckedUpdates = true;
                _currentProgress = 0;
                _totalProgress = 0;
                _ = UpdateGitInfoAsync();
            }
        }

        public void Update()
        {
            if ((DateTime.Now - _lastPeriodicCheckAttempt).TotalMinutes < 1)
                return;

            _lastPeriodicCheckAttempt = DateTime.Now;

            if (Settings.ShouldPerformPeriodicCheck())
            {
                Settings.LastUpdateCheck = DateTime.Now;
                _ = UpdateGitInfoAsync();
            }
        }

        private async Task UpdateGitInfoAsync()
        {
            if (_isUpdating) return;

            try
            {
                _isUpdating = true;
                await updater.UpdateGitInfoAsync();

                var plugins = updater.GetPluginInfo();
                int updateCount = plugins.Count(p => p.CurrentCommit != p.LatestCommit);
                if (updateCount > 0)
                {
                    consoleLog.AddLogMessage("Pending Updates", $"There is {updateCount} plugin {(updateCount > 1 ? "updates" : "update")} pending.", ConsoleLog.ColorInfo, NotificationType.Info);
                }
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

                if (ImGui.BeginTabItem("Settings"))
                {
                    ImGui.Spacing();
                    RenderSettings();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            consoleLog.RenderConsoleLog();
        }

        private void RenderSettings()
        {
            bool checkStartup = Settings.CheckUpdatesOnStartup;
            if (ImGui.Checkbox("Check for updates on startup", ref checkStartup))
            {
                Settings.CheckUpdatesOnStartup = checkStartup;
                Settings.HasCheckedUpdates = false;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Automatically check for plugin updates when the game starts");
            }

            ImGui.Spacing();

            bool autoCheck = Settings.AutoCheckUpdates;
            if (ImGui.Checkbox("Automatically check for updates periodically", ref autoCheck))
            {
                Settings.AutoCheckUpdates = autoCheck;
            }

            if (autoCheck)
            {
                ImGui.Spacing();

                int interval = Settings.UpdateCheckIntervalMinutes;

                int[] intervals = [15, 30, 60, 120, 180, 360, 720, 1440];
                int currentIndex = Array.BinarySearch(intervals, interval);
                if (currentIndex < 0) currentIndex = 0;

                string[] intervalStrings = intervals.Select(i =>
                    i < 60 ? $"{i} minutes" :
                    i == 60 ? "1 hour" :
                    i < 1440 ? $"{i / 60} hours" :
                    "24 hours").ToArray();

                ImGui.SetNextItemWidth(120);
                if (ImGui.BeginCombo("Check every", intervalStrings[currentIndex], ImGuiComboFlags.PopupAlignLeft))
                {
                    for (int i = 0; i < intervalStrings.Length; i++)
                    {
                        bool isSelected = i == currentIndex;
                        if (ImGui.Selectable(intervalStrings[i], isSelected))
                        {
                            Settings.UpdateCheckIntervalMinutes = intervals[i];
                        }

                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }

            ImGui.Spacing();

            bool notificationCheck = Settings.ShowNotifications;
            if (ImGui.Checkbox("Show notifications (drawn bottom right of window)", ref notificationCheck))
            {
                Settings.ShowNotifications = notificationCheck;
            }
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

        private static readonly System.Text.Json.JsonSerializerOptions _serializerOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private async Task LoadRepositoriesAsync()
        {
            if (_isLoadingRepos || _hasLoadedRepos) return;

            try
            {
                _isLoadingRepos = true;
                _loadError = string.Empty;

                using var client = new System.Net.Http.HttpClient();
                var response = await client.GetStringAsync("https://raw.githubusercontent.com/exCore2/PluginBrowserData/refs/heads/data/output.json");

                var repoData = System.Text.Json.JsonSerializer.Deserialize<PluginRepositoryData>(
                    response,
                    _serializerOptions
                );

                _availablePlugins.Clear();
                if (repoData?.PluginDescriptions != null)
                {
                    _availablePlugins.AddRange(repoData.PluginDescriptions);
                }

                _hasLoadedRepos = true;
                consoleLog.LogSuccess($"Successfully loaded {_availablePlugins.Count} plugins");
            }
            catch (Exception ex)
            {
                _loadError = $"Error loading plugins: {ex.Message}";
                consoleLog.LogError(_loadError);
            }
            finally
            {
                _isLoadingRepos = false;
            }
        }

        private string _pluginToDelete = null;

        private void RenderPluginBrowser()
        {
            if (!_hasLoadedRepos && !_isLoadingRepos)
            {
                _ = LoadRepositoriesAsync();
            }

            ImGui.TextWrapped("Browse and download available plugins from the ExileCore2 organization.");
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
                            ImGuiTableFlags.RowBg |
                            ImGuiTableFlags.Hideable;

            const float ROW_HEIGHT = 30;
            const float HEADER_HEIGHT = 30;
            var installedPlugins = updater.GetPluginInfo();

            if (_availablePlugins.Count == 0)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.2f, 0.2f, 1.0f), "No plugins found in the repository.");
                return;
            }

            float tableHeight = (_availablePlugins.Count * ROW_HEIGHT) + HEADER_HEIGHT;

            if (!ImGui.BeginTable("##browsertable", 6, tableFlags, new System.Numerics.Vector2(-1, tableHeight)))
                return;

            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("Original Author", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Endorsed Fork", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Last Updated", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            foreach (var plugin in _availablePlugins)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(plugin.Name);

                ImGui.TableNextColumn();
                ImGui.TextWrapped(string.IsNullOrEmpty(plugin.Description) ? "-" : plugin.Description);

                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrEmpty(plugin.OriginalAuthor) ? "-" : plugin.OriginalAuthor);

                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrEmpty(plugin.EndorsedAuthor) ? "-" : plugin.EndorsedAuthor);

                var endorsedFork = string.IsNullOrWhiteSpace(plugin.EndorsedAuthor)
                    ? plugin.Forks?.FirstOrDefault()
                    : plugin.Forks?.FirstOrDefault(f => f.Author == plugin.EndorsedAuthor);

                ImGui.TableNextColumn();
                var lastUpdated = endorsedFork?.LatestCommit?.Date ?? "-";
                if (lastUpdated != "-")
                {
                    lastUpdated = DateTime.Parse(lastUpdated).ToString("yyyy-MM-dd");
                }
                ImGui.Text(lastUpdated);

                ImGui.TableNextColumn();

                if (endorsedFork == null) continue;

                bool isInstalled = installedPlugins.Any(ip =>
                    ip.Name.Equals(endorsedFork.Name, StringComparison.OrdinalIgnoreCase));

                if (isInstalled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.2f, 0.2f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1.0f));

                    if (ImGui.Button($"Delete##{endorsedFork.Name}"))
                    {
                        _pluginToDelete = endorsedFork.Name;
                        ImGui.OpenPopup("Delete Plugin?");
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Delete this plugin");
                    }

                    ImGui.PopStyleColor(3);
                }
                else
                {
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
                            string cloneUrl = $"https://github.com/{endorsedFork.Location}/{endorsedFork.Name}.git";
                            _ = DownloadPluginAsync(plugin.Name, cloneUrl);
                        }

                        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(endorsedFork.LatestCommit?.Message))
                        {
                            ImGui.SetTooltip($"Latest commit: {endorsedFork.LatestCommit.Message}");
                        }

                        ImGui.PopStyleColor(3);
                    }
                }
            }

            if (ImGui.BeginPopupModal("Delete Plugin?"))
            {
                ImGui.Text($"Are you sure you want to delete the plugin '{_pluginToDelete}'?");
                ImGui.Text("This action cannot be undone!");
                ImGui.Spacing();

                float buttonWidth = 120;
                float spacing = 20;
                float totalWidth = (buttonWidth * 2) + spacing;
                ImGui.SetCursorPosX((ImGui.GetWindowSize().X - totalWidth) * 0.5f);

                ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.2f, 0.2f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1.0f));

                if (ImGui.Button("Delete", new System.Numerics.Vector2(buttonWidth, 0)))
                {
                    try
                    {
                        updater.DeletePlugin(_pluginToDelete);
                        consoleLog.LogSuccess($"Successfully deleted plugin: {_pluginToDelete}");
                        ImGui.CloseCurrentPopup();
                    }
                    catch (Exception ex)
                    {
                        consoleLog.LogError($"Failed to delete plugin: {ex.Message}");
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.PopStyleColor(3);

                ImGui.SameLine(0, spacing);
                if (ImGui.Button("Cancel", new System.Numerics.Vector2(buttonWidth, 0)))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }

            ImGui.EndTable();
        }

        private async Task DownloadPluginAsync(string pluginName, string cloneUrl)
        {
            if (_downloadingPlugins.TryGetValue(pluginName, out bool isDownloading) && isDownloading)
                return;

            try
            {
                _downloadingPlugins[pluginName] = true;
                consoleLog.LogInfo($"Downloading plugin: {pluginName}");

                await updater.CloneRepositoryAsync(cloneUrl);

                consoleLog.LogSuccess($"Successfully downloaded {pluginName}");
            }
            catch (Exception ex)
            {
                consoleLog.LogError($"Error downloading plugin {pluginName}: {ex.Message}");
            }
            finally
            {
                _downloadingPlugins[pluginName] = false;
            }
        }


        public void Dispose()
        {
            updater?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}