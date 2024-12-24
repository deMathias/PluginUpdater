using ExileCore2;
using System;

namespace WheresMyPluginsAt;

public class WheresMyPluginsAt : BaseSettingsPlugin<WheresMyPluginsAtSettings>
{
    public override bool Initialise()
    {
        Settings.GameController = GameController;
        Settings.PluginConfig.Startup();
        return true;
    }

    public override void Render()
    {
        Settings.PluginConfig.Update();
        
        if(Settings.ShowNotifications)
            Settings.PluginConfig.consoleLog.RenderNotifications(GameController.Window.GetWindowRectangleReal());
    }
}