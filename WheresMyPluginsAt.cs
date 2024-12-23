using ExileCore2;

namespace WheresMyPluginsAt;

public class WheresMyPluginsAt : BaseSettingsPlugin<WheresMyPluginsAtSettings>
{
    public override bool Initialise()
    {
        //xd
        Settings.GameController = GameController;
        return true;
    }

    public override void Render()
    {
        
    }
}