using ExileCore2;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace WheresMyPluginsAt;

public class WheresMyPluginsAtSettings : ISettings
{
    public WheresMyPluginsAtSettings()
    {
        PluginConfig = new PluginRenderer(this);
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [JsonIgnore]
    public PluginRenderer PluginConfig { get; set; }
    [JsonIgnore]
    public GameController GameController { get; set; } // this is very lazy
}

