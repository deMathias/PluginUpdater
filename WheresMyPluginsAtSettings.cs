using ExileCore2;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace WheresMyPluginsAt;

public class WheresMyPluginsAtSettings : ISettings
{
    public WheresMyPluginsAtSettings()
    {
        PluginConfig = new PluginRenderer(this);
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    public PluginRenderer PluginConfig { get; set; } = null;
    public GameController GameController { get; set; } = null; // this is very lazy
}

