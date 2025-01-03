using ExileCore2;

namespace PluginUpdater;

public class PluginUpdater : BaseSettingsPlugin<PluginUpdaterSettings>
{
    public static PluginUpdater Instance;

    public override bool Initialise()
    {
        Instance = this;
        Settings.GameController = GameController;
        Settings.PluginConfig.Startup();
        return true;
    }

    public override void Render()
    {
        Settings.PluginConfig.Update();
    }
}