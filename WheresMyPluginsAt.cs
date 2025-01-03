using ExileCore2;

namespace WheresMyPluginsAt;

public class WheresMyPluginsAt : BaseSettingsPlugin<WheresMyPluginsAtSettings>
{
    public static WheresMyPluginsAt Instance;

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