using Server.Network;

namespace Server.Commands.SetWeather;

public static class SetWeatherCommand
{
    public static void Configure()
    {
        CommandSystem.Register("SetWeather", AccessLevel.GameMaster, OnCommand);
    }

    // [SetWeather <type> [density] [temperature]
    // type: 0 rain, 1 fierce storm, 2 snow, 3 a storm is brewing, 0xFE/0xFF off
    [Usage("SetWeather <type> [density 0-70] [temperature]")]
    [Description("Sends a weather packet to you only, to test how the client draws weather.")]
    private static void OnCommand(CommandEventArgs e)
    {
        if (e.Length < 1)
        {
            e.Mobile.SendMessage("Usage: SetWeather <type> [density 0-70] [temperature]");
            return;
        }

        var type = e.GetInt32(0);
        var density = e.Length > 1 ? e.GetInt32(1) : 70;
        var temperature = e.Length > 2 ? e.GetInt32(2) : 15;

        e.Mobile.NetState.SendWeather((byte)type, (byte)density, (byte)temperature);
        e.Mobile.SendMessage($"Weather {type}, density {density}, temperature {temperature} sent.");
    }
}
